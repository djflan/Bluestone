#!/usr/bin/env python3
"""Writes format-1 and format-0 16-channel General MIDI import fixtures for Bluestone.

An original arrangement of Pachelbel's Canon in D (public domain), released under the repository's
MIT licence. It deliberately exercises every MIDI channel and the parts of a Standard MIDI File that
Bluestone imports: GM System On SysEx, bank select and program changes, mixer controllers, an RPN
pitch-bend range, pitch bend, channel pressure, sustain pedal, expression, lyrics, key signature,
copyright and text meta events, tempo and meter changes, markers, running status, both note-off
styles (0x80 and note-on velocity 0), and same-note retriggers on the tick of the previous release.

The file is written byte by byte with the standard library only, independent of Bluestone's own
writer. Usage: python3 src/dotnet/TestFixtures/Midi/canon-gm16.py  (writes both MIDI files next to this script)
"""

import pathlib

PPQN = 480
BEAT = PPQN
BAR = 4 * BEAT
CYCLE = 2 * BAR
CODA = 16 * BAR
END = CODA + 3 * BEAT

TITLE = "Canon in D (after Pachelbel)"

PITCH_CLASSES = {"C": 0, "C#": 1, "D": 2, "D#": 3, "E": 4, "F": 5, "F#": 6, "G": 7, "G#": 8, "A": 9, "A#": 10, "B": 11}

# One chord per beat of the two-bar ground: D A Bm F#m G D G A (root, third, fifth).
CHORDS = [("D", "F#", "A"), ("A", "C#", "E"), ("B", "D", "F#"), ("F#", "A", "C#"),
          ("G", "B", "D"), ("D", "F#", "A"), ("G", "B", "D"), ("A", "C#", "E")]
GROUND = ["D3", "A2", "B2", "F#2", "G2", "D2", "G2", "A2"]
HARPSICHORD_VOICINGS = [("D4", "F#4", "A4"), ("A3", "C#4", "E4"), ("B3", "D4", "F#4"), ("A3", "C#4", "F#4"),
                        ("G3", "B3", "D4"), ("A3", "D4", "F#4"), ("G3", "B3", "D4"), ("A3", "C#4", "E4")]

VARIATIONS = [
    (BEAT, "F#5 E5 D5 C#5 B4 A4 B4 C#5"),
    (BEAT, "D5 C#5 B4 A4 G4 F#4 G4 E4"),
    (BEAT // 2, "D4 F#4 A4 G4 F#4 D4 F#4 E4 D4 B3 D4 A4 G4 B4 A4 G4"),
    (BEAT // 2, "F#5 D5 E5 C#5 D5 B4 C#5 A4 B4 G4 A4 F#4 G4 B4 A4 C#5"),
    (BEAT // 4, "D5 F#5 A5 F#5 C#5 E5 A5 E5 B4 D5 F#5 D5 A4 C#5 F#5 C#5 "
                "B4 D5 G5 D5 A4 D5 F#5 D5 B4 D5 G5 D5 C#5 E5 A5 E5"),
    (BEAT, "D6 C#6 B5 A5 G5 F#5 G5 E5"),
    (BEAT, "F#5 E5 D5 C#5 B4 A4 B4 C#5"),
]


def note(name):
    return 12 * (int(name[-1]) + 1) + PITCH_CLASSES[name[:-1]]


def bar(number):
    return (number - 1) * BAR


def vlq(value):
    out = [value & 0x7F]
    value >>= 7
    while value:
        out.append(0x80 | (value & 0x7F))
        value >>= 7
    return bytes(reversed(out))


def text(value):
    return value.encode("utf-8")


class Track:
    # Same-tick order: meta and SysEx, then controllers and programs, then releases, then attacks.
    META, CONTROL, OFF, ON = range(4)

    def __init__(self, name, channel=None):
        self.name = name
        self.channel = None if channel is None else channel - 1
        self.events = []
        self.notes = 0
        self.lyrics = 0
        self.meta(0, 0x03, text(name))

    def _add(self, tick, order, kind, payload):
        self.events.append((tick, order, len(self.events), kind, payload))

    def meta(self, tick, kind, data):
        self._add(tick, Track.META, "meta", (kind, data))

    def sysex(self, tick, data):
        self._add(tick, Track.META, "sysex", data)

    def _channel(self, tick, order, status, *data):
        self._add(tick, order, "channel", bytes([status | self.channel, *data]))

    def cc(self, tick, controller, value):
        self._channel(tick, Track.CONTROL, 0xB0, controller, value)

    def program(self, tick, value):
        self._channel(tick, Track.CONTROL, 0xC0, value)

    def pressure(self, tick, value):
        self._channel(tick, Track.CONTROL, 0xD0, value)

    def bend(self, tick, value):
        self._channel(tick, Track.CONTROL, 0xE0, value & 0x7F, value >> 7)

    def play(self, tick, length, key, velocity, explicit_off=False, lyric=None):
        key = note(key) if isinstance(key, str) else key
        if lyric is not None:
            self.meta(tick, 0x05, text(lyric))
            self.lyrics += 1
        self._channel(tick, Track.ON, 0x90, key, velocity)
        if explicit_off:
            self._channel(tick + length, Track.OFF, 0x80, key, 64)
        else:
            self._channel(tick + length, Track.OFF, 0x90, key, 0)
        self.notes += 1

    def setup(self, program, volume, pan, reverb, chorus):
        for controller, value in ((0, 0), (32, 0)):
            self.cc(0, controller, value)
        self.program(0, program)
        for controller, value in ((7, volume), (10, pan), (91, reverb), (93, chorus)):
            self.cc(0, controller, value)

    def encode(self):
        body = bytearray()
        last_tick = 0
        running = None
        for tick, _, _, kind, payload in sorted(self.events):
            body += vlq(tick - last_tick)
            last_tick = tick
            if kind == "meta":
                meta_type, data = payload
                body += bytes([0xFF, meta_type]) + vlq(len(data)) + data
                running = None
            elif kind == "sysex":
                body += bytes([0xF0]) + vlq(len(payload)) + payload
                running = None
            else:
                if payload[0] != running:
                    body.append(payload[0])
                    running = payload[0]
                body += payload[1:]
        body += vlq(END - last_tick) + bytes([0xFF, 0x2F, 0x00])
        return b"MTrk" + len(body).to_bytes(4, "big") + bytes(body)


def melody(track, start, variation, transpose=0, velocity=84, gap=10, explicit_off=False):
    step, notes = variation
    for i, name in enumerate(notes.split()):
        accent = 8 if (i * step) % BAR == 0 else 0
        track.play(start + i * step, step - gap, note(name) + transpose, velocity + accent, explicit_off)


def build():
    conductor = Track(TITLE)
    for tick, bpm_microseconds in ((0, 750_000), (bar(15), 800_000), (CODA, 1_000_000)):
        conductor.meta(tick, 0x51, bpm_microseconds.to_bytes(3, "big"))
    conductor.meta(0, 0x58, bytes([4, 2, 24, 8]))
    conductor.meta(CODA, 0x58, bytes([3, 2, 24, 8]))
    for tick, name in ((0, "Ground"), (bar(3), "Canon"), (bar(9), "Tutti"), (CODA, "Coda")):
        conductor.meta(tick, 0x06, text(name))

    parts = {}

    def part(channel, name, program, volume, pan, reverb=40, chorus=0):
        track = Track(name, channel)
        parts[channel] = track
        return track, (program, volume, pan, reverb, chorus)

    violin1, mix = part(1, "Violin I", 40, 100, 40, 60, 10)
    violin1.sysex(0, bytes([0x7E, 0x7F, 0x09, 0x01, 0xF7]))
    violin1.meta(0, 0x59, bytes([2, 0]))
    violin1.meta(0, 0x02, text("Arrangement © Bluestone contributors, MIT licence"))
    violin1.meta(0, 0x01, text("Pachelbel, Canon in D (public domain), arranged for 16 GM channels."))
    violin1.setup(*mix)
    for i, variation in enumerate(VARIATIONS):
        melody(violin1, bar(3) + i * CYCLE, variation)
    violin1.play(CODA, 3 * BEAT, "D5", 90)

    violin2, mix = part(2, "Violin II", 40, 96, 88, 60, 10)
    violin2.setup(*mix)
    for i, variation in enumerate(VARIATIONS[:6]):
        melody(violin2, bar(5) + i * CYCLE, variation, velocity=80)
    violin2.play(CODA, 3 * BEAT, "A4", 86)

    viola, mix = part(3, "Viola", 41, 92, 56, 60, 10)
    viola.setup(*mix)
    for i, variation in enumerate(VARIATIONS[:5]):
        melody(viola, bar(7) + i * CYCLE, variation, velocity=76)
    viola.play(CODA, 3 * BEAT, "F#4", 84)

    cello, mix = part(4, "Cello", 42, 100, 72, 50)
    cello.setup(*mix)
    for cycle in range(8):
        for beat, name in enumerate(GROUND):
            cello.play(cycle * CYCLE + beat * BEAT, BEAT, name, 92 if beat == 0 else 84)
    cello.play(CODA, 3 * BEAT, "D2", 96)

    bass, mix = part(5, "Contrabass", 43, 90, 64, 40)
    bass.setup(*mix)
    for cycle in range(1, 8):
        for half, name in enumerate(("D2", "B1", "G1", "G1")):
            bass.play(cycle * CYCLE + half * 2 * BEAT, 2 * BEAT, name, 88, explicit_off=True)
    bass.play(CODA, 3 * BEAT, "D2", 92, explicit_off=True)

    harpsichord, mix = part(6, "Harpsichord", 6, 70, 30, 30)
    harpsichord.setup(*mix)
    for cycle in range(8):
        for beat, voicing in enumerate(HARPSICHORD_VOICINGS):
            for name in voicing:
                harpsichord.play(cycle * CYCLE + beat * BEAT, BEAT // 2, name, 72, explicit_off=True)
    for name in ("D4", "F#4", "A4"):
        harpsichord.play(CODA, 3 * BEAT, name, 76, explicit_off=True)

    piano, mix = part(7, "Piano", 0, 80, 64, 50)
    piano.setup(*mix)
    for cycle in range(2, 8):
        start = cycle * CYCLE
        piano.cc(start, 64, 0)
        piano.cc(start + 30, 64, 127)
        for beat, (root, third, fifth) in enumerate(CHORDS):
            tick = start + beat * BEAT
            piano.play(tick, 220, note(root + "3"), 70)
            piano.play(tick + BEAT // 2, 220, note(third + "4"), 60)
            piano.play(tick + BEAT // 2, 220, note(fifth + "4"), 60)
    piano.cc(CODA, 64, 0)
    piano.cc(CODA + 30, 64, 127)
    for name in ("D3", "D4", "F#4", "A4"):
        piano.play(CODA, 3 * BEAT - 60, name, 74)
    piano.cc(END - 30, 64, 0)

    strings, mix = part(8, "Strings", 48, 75, 64, 70, 20)
    strings.setup(*mix)
    for step in range(64):
        strings.cc(bar(9) + step * 60, 11, 30 + round(97 * step / 63))
    for cycle in range(4, 8):
        for beat, voicing in enumerate(HARPSICHORD_VOICINGS):
            for name in voicing:
                strings.play(cycle * CYCLE + beat * BEAT, BEAT, name, 64)
    for name in ("D4", "F#4", "A4", "D5"):
        strings.play(CODA, 3 * BEAT, name, 70)

    flute, mix = part(9, "Flute", 73, 90, 50, 60)
    flute.setup(*mix)
    for controller, value in ((101, 0), (100, 0), (6, 2), (38, 0), (101, 127), (100, 127)):
        flute.cc(0, controller, value)
    melody(flute, bar(11), VARIATIONS[1], transpose=12, velocity=80)
    for step in range(9):
        flute.bend(bar(13) + step * 15, 4096 + step * 512)
    melody(flute, bar(13), VARIATIONS[0], transpose=12, velocity=84)
    melody(flute, bar(15), VARIATIONS[3], transpose=12, velocity=84)
    flute.play(CODA, 3 * BEAT, "A5", 86)

    drums, mix = part(10, "Drums", 0, 85, 64, 30)
    drums.setup(*mix)
    for number in range(9, 17):
        start = bar(number)
        if number in (9, 13):
            drums.play(start, 60, 49, 110)
        for beat in range(4):
            drums.play(start + beat * BEAT, 60, 36 if beat % 2 == 0 else 37, 100 if beat % 2 == 0 else 80)
        if number < 13:
            for eighth in range(8):
                drums.play(start + eighth * BEAT // 2, 60, 42, 70 if eighth % 2 == 0 else 50)
        else:
            for beat in range(4):
                drums.play(start + beat * BEAT, 60, 51, 75)
    for i, velocity in enumerate((70, 80, 90, 100)):
        drums.play(bar(16) + 3 * BEAT + i * BEAT // 4, 60, 38, velocity)
    drums.play(CODA, 60, 49, 115)
    drums.play(CODA, 60, 36, 110)

    oboe, mix = part(11, "Oboe", 68, 85, 78, 50)
    oboe.setup(*mix)
    for cycle in range(6, 8):
        tick = cycle * CYCLE
        for name, beats in (("A4", 2), ("F#4", 2), ("D5", 2), ("B4", 1), ("A4", 1)):
            oboe.pressure(tick, 40)
            oboe.pressure(tick + BEAT // 2, 70)
            oboe.play(tick, beats * BEAT - 20, name, 82, explicit_off=True)
            tick += beats * BEAT
    oboe.pressure(CODA, 0)
    oboe.play(CODA, 3 * BEAT, "F#5", 84, explicit_off=True)

    horn, mix = part(12, "French Horn", 60, 80, 90, 60)
    horn.setup(*mix)
    for cycle in range(4, 8):
        tick = cycle * CYCLE
        for name, beats in (("A3", 2), ("F#3", 2), ("D4", 2), ("D4", 1), ("C#4", 1)):
            horn.play(tick, beats * BEAT, name, 74)
            tick += beats * BEAT
    horn.play(CODA, 3 * BEAT, "A3", 80)

    harp, mix = part(13, "Harp", 46, 85, 20, 70)
    harp.setup(*mix)
    for cycle in range(3, 8):
        for beat, (root, third, _) in enumerate(CHORDS):
            low = note(root + "3")
            high_third = note(third + "4")
            if high_third < low + 12:
                high_third += 12
            for i, key in enumerate((low, low + 7, low + 12, high_third)):
                harp.play(cycle * CYCLE + beat * BEAT + i * BEAT // 4, 110, key, 72 if i == 0 else 62)
    for i, name in enumerate(("D3", "A3", "D4", "F#4", "A4", "D5")):
        harp.play(CODA + i * 30, 3 * BEAT - i * 30, name, 76)

    choir, mix = part(14, "Choir", 52, 70, 64, 80, 30)
    choir.setup(*mix)
    for cycle in range(6, 8):
        tick = cycle * CYCLE
        for name, beats in (("A3", 2), ("F#3", 2), ("D4", 2), ("B3", 1), ("A3", 1)):
            choir.play(tick, beats * BEAT - 10, name, 70, lyric="Ah")
            choir.play(tick, beats * BEAT - 10, note(name) + 12, 66)
            tick += beats * BEAT
    choir.play(CODA, 3 * BEAT, "D4", 74, lyric="Ah")
    choir.play(CODA, 3 * BEAT, "F#4", 70)
    choir.play(CODA, 3 * BEAT, "A4", 70)

    timpani, mix = part(15, "Timpani", 47, 95, 64, 50)
    timpani.setup(*mix)
    for cycle in range(4, 8):
        start = cycle * CYCLE
        timpani.play(start, BEAT, "D3", 96)
        if cycle < 7:
            timpani.play(start + 7 * BEAT, BEAT, "A2", 88)
    for i in range(16):
        timpani.play(bar(16) + 2 * BEAT + i * BEAT // 8, 50, "A2", 50 + i * 4)
    timpani.play(CODA, BEAT, "D3", 112)

    glockenspiel, mix = part(16, "Glockenspiel", 9, 70, 100, 70)
    glockenspiel.setup(*mix)
    melody(glockenspiel, bar(15), VARIATIONS[0], transpose=12, velocity=70, gap=280, explicit_off=True)
    glockenspiel.play(CODA, 3 * BEAT, "D6", 76, explicit_off=True)

    return conductor, [parts[channel] for channel in range(1, 17)]


def main():
    conductor, tracks = build()
    header = b"MThd" + (6).to_bytes(4, "big") + (1).to_bytes(2, "big") + (1 + len(tracks)).to_bytes(2, "big") + PPQN.to_bytes(2, "big")
    data = header + conductor.encode() + b"".join(track.encode() for track in tracks)
    path = pathlib.Path(__file__).with_name("canon-gm16.mid")
    path.write_bytes(data)

    merged = Track(TITLE)
    merged.events = []
    for track in [conductor, *tracks]:
        for tick, order, _, kind, payload in track.events:
            if track is not conductor and kind == "meta" and payload[0] == 0x03:
                continue
            merged._add(tick, order, kind, payload)
    format0_header = b"MThd" + (6).to_bytes(4, "big") + bytes([0, 0, 0, 1]) + PPQN.to_bytes(2, "big")
    format0_path = path.with_name("canon-gm16-format0.mid")
    format0_path.write_bytes(format0_header + merged.encode())
    print(f"Wrote {format0_path.name}: one physical track, {len(tracks)} MIDI channels")

    print(f"Wrote {path.name}: {len(data)} bytes, {len(tracks)} tracks, {sum(t.notes for t in tracks)} notes")
    for track in tracks:
        print(f"  ch {track.channel + 1:2}  {track.name:<13} {track.notes:4} notes  {track.lyrics} lyrics")


if __name__ == "__main__":
    main()
