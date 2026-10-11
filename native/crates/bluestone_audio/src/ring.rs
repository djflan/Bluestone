//! A single-producer, single-consumer ring of interleaved f32 frames between a .NET render thread (writer) and the
//! device callback (reader). Used by buffered streams: the callback only copies from here, so a pause of the .NET
//! runtime (a garbage collection) delays the writer but not the device, as long as the ring holds enough audio.

use std::cell::UnsafeCell;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicU64, Ordering};
use std::thread::Thread;
use std::time::Duration;

pub struct Ring {
    data: Box<[UnsafeCell<f32>]>,
    capacity: u64,
    channels: usize,
    write: AtomicU64,
    read: AtomicU64,
    underrun_frames: AtomicU64,
    wake_below: AtomicU64,
    writer: OnceLock<Thread>,
}

// SAFETY: the writer only touches frames in [write, read + capacity) and the reader only frames in [read, write);
// the positions are published with release/acquire ordering, so the two never access the same sample at once.
unsafe impl Sync for Ring {}

impl Ring {
    pub fn new(capacity_frames: usize, channels: usize) -> Ring {
        let capacity_frames = capacity_frames.max(1);
        Ring {
            data: (0..capacity_frames * channels.max(1))
                .map(|_| UnsafeCell::new(0.0))
                .collect(),
            capacity: capacity_frames as u64,
            channels: channels.max(1),
            write: AtomicU64::new(0),
            read: AtomicU64::new(0),
            underrun_frames: AtomicU64::new(0),
            wake_below: AtomicU64::new(0),
            writer: OnceLock::new(),
        }
    }

    pub fn capacity(&self) -> u64 {
        self.capacity
    }

    /// Frames written and not yet played. Safe from any thread: `read` is loaded first, and the reader only stores a
    /// `read` it saw `write` reach, so the later `write` load is never behind it (the saturation is only a guard).
    /// From a third thread both positions can move between the two loads, so the result is clamped to the capacity.
    pub fn fill(&self) -> u64 {
        let read = self.read.load(Ordering::Acquire);
        let write = self.write.load(Ordering::Acquire);
        write.saturating_sub(read).min(self.capacity)
    }

    /// Frames the device found missing (played as silence) since the stream started.
    pub fn underrun_frames(&self) -> u64 {
        self.underrun_frames.load(Ordering::Relaxed)
    }

    /// Reader (device callback): fills `out` (interleaved) from the ring, silence for what is missing. No allocation,
    /// no locks; it wakes the writer when the fill drops below what the writer waits for.
    pub fn read(&self, out: &mut [f32]) {
        let frames = (out.len() / self.channels) as u64;
        let read = self.read.load(Ordering::Relaxed);
        let available = self.write.load(Ordering::Acquire) - read;
        let n = frames.min(available);
        for i in 0..n {
            let slot = (((read + i) % self.capacity) as usize) * self.channels;
            for c in 0..self.channels {
                // SAFETY: frames in [read, write) belong to the reader until `read` is advanced.
                out[i as usize * self.channels + c] = unsafe { *self.data[slot + c].get() };
            }
        }
        out[(n as usize * self.channels)..].fill(0.0);
        if n < frames {
            self.underrun_frames
                .fetch_add(frames - n, Ordering::Relaxed);
        }
        self.read.store(read + n, Ordering::Release);
        if available - n < self.wake_below.load(Ordering::Relaxed)
            && let Some(writer) = self.writer.get()
        {
            writer.unpark();
        }
    }

    /// Writer: copies as many whole frames of `input` (interleaved) as fit; returns how many.
    pub fn write(&self, input: &[f32]) -> u64 {
        let frames = (input.len() / self.channels) as u64;
        let write = self.write.load(Ordering::Relaxed);
        let space = self.capacity - (write - self.read.load(Ordering::Acquire));
        let n = frames.min(space);
        for i in 0..n {
            let slot = (((write + i) % self.capacity) as usize) * self.channels;
            for c in 0..self.channels {
                // SAFETY: frames in [write, read + capacity) belong to the writer until `write` is advanced.
                unsafe { *self.data[slot + c].get() = input[i as usize * self.channels + c] };
            }
        }
        self.write.store(write + n, Ordering::Release);
        n
    }

    /// Writer: sleeps until the fill is below `below` frames or `timeout` passes; returns the fill.
    pub fn wait(&self, below: u64, timeout: Duration) -> u64 {
        let _ = self.writer.get_or_init(std::thread::current);
        self.wake_below.store(below, Ordering::Relaxed);
        let fill = self.fill();
        if fill >= below {
            std::thread::park_timeout(timeout);
        }
        self.fill()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames_come_out_in_order_across_the_wrap() {
        let ring = Ring::new(4, 2);
        assert_eq!(ring.write(&[1.0, 1.5, 2.0, 2.5, 3.0, 3.5]), 3);
        let mut out = [0.0; 4];
        ring.read(&mut out);
        assert_eq!(out, [1.0, 1.5, 2.0, 2.5]);
        assert_eq!(ring.write(&[4.0, 4.5, 5.0, 5.5, 6.0, 6.5, 7.0, 7.5]), 3);
        let mut out = [0.0; 8];
        ring.read(&mut out);
        assert_eq!(out, [3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 6.5]);
        assert_eq!(ring.underrun_frames(), 0);
    }

    #[test]
    fn a_short_ring_plays_silence_for_the_missing_frames_and_counts_them() {
        let ring = Ring::new(8, 1);
        ring.write(&[1.0, 2.0]);
        let mut out = [9.0; 5];
        ring.read(&mut out);
        assert_eq!(out, [1.0, 2.0, 0.0, 0.0, 0.0]);
        assert_eq!(ring.underrun_frames(), 3);
        assert_eq!(ring.fill(), 0);
    }

    #[test]
    fn a_full_ring_takes_nothing_more() {
        let ring = Ring::new(2, 1);
        assert_eq!(ring.write(&[1.0, 2.0, 3.0]), 2);
        assert_eq!(ring.write(&[4.0]), 0);
    }

    #[test]
    fn a_third_thread_never_sees_more_than_the_capacity_queued() {
        use std::sync::Arc;
        use std::sync::atomic::AtomicBool;
        let ring = Arc::new(Ring::new(4, 1));
        let stop = Arc::new(AtomicBool::new(false));
        let writer = {
            let (ring, stop) = (Arc::clone(&ring), Arc::clone(&stop));
            std::thread::spawn(move || {
                while !stop.load(Ordering::Relaxed) {
                    ring.write(&[0.0; 4]);
                }
            })
        };
        let reader = {
            let (ring, stop) = (Arc::clone(&ring), Arc::clone(&stop));
            std::thread::spawn(move || {
                let mut out = [0.0; 1];
                while !stop.load(Ordering::Relaxed) {
                    ring.read(&mut out);
                }
            })
        };
        let started = std::time::Instant::now();
        let mut worst = 0;
        while started.elapsed() < Duration::from_millis(500) {
            worst = worst.max(ring.fill());
        }
        stop.store(true, Ordering::Relaxed);
        writer.join().unwrap();
        reader.join().unwrap();
        assert!(worst <= ring.capacity(), "observed fill {worst}");
    }

    #[test]
    fn the_reader_wakes_a_waiting_writer() {
        let ring = std::sync::Arc::new(Ring::new(64, 1));
        ring.write(&[0.0; 64]);
        let reader = std::sync::Arc::clone(&ring);
        let started = std::time::Instant::now();
        let consumer = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(20));
            let mut out = [0.0; 48];
            reader.read(&mut out);
        });
        let fill = ring.wait(32, Duration::from_secs(5));
        consumer.join().unwrap();
        assert!(fill < 32 || started.elapsed() < Duration::from_secs(5));
        assert!(started.elapsed() < Duration::from_secs(4));
    }
}
