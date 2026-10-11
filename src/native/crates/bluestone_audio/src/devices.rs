//! Output device enumeration, as JSON for the .NET side.

use crate::json;
use cpal::traits::{DeviceTrait, HostTrait};
use cpal::{SupportedBufferSize, SupportedStreamConfigRange};

/// Sample rates offered when a device reports a continuous range.
pub(crate) const STANDARD_RATES: [u32; 11] = [
    8_000, 11_025, 16_000, 22_050, 32_000, 44_100, 48_000, 88_200, 96_000, 176_400, 192_000,
];

/// What one device supports, summarised from cpal's configuration ranges.
#[derive(Debug, Default, PartialEq, Eq)]
pub(crate) struct Capabilities {
    pub sample_rates: Vec<u32>,
    pub channel_counts: Vec<u32>,
    pub min_buffer_frames: Option<u32>,
    pub max_buffer_frames: Option<u32>,
}

/// One configuration range: minimum rate, maximum rate, channels, and the buffer size range when known.
pub(crate) type Range = (u32, u32, u32, Option<(u32, u32)>);

/// Folds configuration ranges (rate range, channels, buffer range) into lists a user can choose from.
pub(crate) fn summarize(ranges: &[Range]) -> Capabilities {
    let mut caps = Capabilities::default();
    for &(min_rate, max_rate, channels, buffer) in ranges {
        for rate in STANDARD_RATES {
            if rate >= min_rate && rate <= max_rate && !caps.sample_rates.contains(&rate) {
                caps.sample_rates.push(rate);
            }
        }
        if min_rate == max_rate && !caps.sample_rates.contains(&min_rate) {
            caps.sample_rates.push(min_rate);
        }
        if !caps.channel_counts.contains(&channels) {
            caps.channel_counts.push(channels);
        }
        if let Some((min, max)) = buffer {
            caps.min_buffer_frames = Some(caps.min_buffer_frames.map_or(min, |m| m.min(min)));
            caps.max_buffer_frames = Some(caps.max_buffer_frames.map_or(max, |m| m.max(max)));
        }
    }
    caps.sample_rates.sort_unstable();
    caps.channel_counts.sort_unstable();
    caps
}

fn range_tuple(range: &SupportedStreamConfigRange) -> Range {
    let buffer = match range.buffer_size() {
        SupportedBufferSize::Range { min, max } => Some((*min, *max)),
        SupportedBufferSize::Unknown => None,
    };
    (
        range.min_sample_rate(),
        range.max_sample_rate(),
        u32::from(range.channels()),
        buffer,
    )
}

/// Every output device of the default host as a JSON array.
pub(crate) fn devices_json() -> String {
    let host = cpal::default_host();
    let default_id = host
        .default_output_device()
        .and_then(|d| d.id().ok())
        .map(|id| id.to_string());
    let mut out = String::from("[");
    let mut first = true;
    if let Ok(devices) = host.output_devices() {
        for device in devices {
            let Ok(id) = device.id() else { continue };
            let id = id.to_string();
            let name = device
                .description()
                .map(|d| d.name().to_string())
                .unwrap_or_else(|_| id.clone());
            let ranges: Vec<_> = device
                .supported_output_configs()
                .map(|configs| configs.map(|r| range_tuple(&r)).collect())
                .unwrap_or_default();
            let caps = summarize(&ranges);
            let default_config = device.default_output_config().ok();
            if !first {
                out.push(',');
            }
            first = false;
            out.push_str("{\"id\":");
            json::string(&mut out, &id);
            out.push_str(",\"name\":");
            json::string(&mut out, &name);
            out.push_str(",\"host\":");
            json::string(&mut out, host.id().name());
            out.push_str(",\"isDefault\":");
            out.push_str(if default_id.as_deref() == Some(id.as_str()) {
                "true"
            } else {
                "false"
            });
            out.push_str(",\"defaultSampleRate\":");
            out.push_str(
                &default_config
                    .as_ref()
                    .map_or(0, |c| c.sample_rate())
                    .to_string(),
            );
            out.push_str(",\"defaultChannels\":");
            out.push_str(
                &default_config
                    .as_ref()
                    .map_or(0, |c| u32::from(c.channels()))
                    .to_string(),
            );
            out.push_str(",\"sampleRates\":");
            json::numbers(&mut out, &caps.sample_rates);
            out.push_str(",\"channelCounts\":");
            json::numbers(&mut out, &caps.channel_counts);
            out.push_str(",\"minBufferFrames\":");
            out.push_str(&caps.min_buffer_frames.unwrap_or(0).to_string());
            out.push_str(",\"maxBufferFrames\":");
            out.push_str(&caps.max_buffer_frames.unwrap_or(0).to_string());
            out.push('}');
        }
    }
    out.push(']');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn summarize_lists_standard_rates_inside_each_range_once() {
        let caps = summarize(&[
            (44_100, 48_000, 2, Some((15, 4096))),
            (44_100, 96_000, 2, Some((32, 8192))),
        ]);
        assert_eq!(caps.sample_rates, vec![44_100, 48_000, 88_200, 96_000]);
        assert_eq!(caps.channel_counts, vec![2]);
        assert_eq!(caps.min_buffer_frames, Some(15));
        assert_eq!(caps.max_buffer_frames, Some(8192));
    }

    #[test]
    fn summarize_keeps_a_fixed_nonstandard_rate() {
        let caps = summarize(&[(47_000, 47_000, 1, None), (48_000, 48_000, 6, None)]);
        assert_eq!(caps.sample_rates, vec![47_000, 48_000]);
        assert_eq!(caps.channel_counts, vec![1, 6]);
        assert_eq!(caps.min_buffer_frames, None);
    }

    #[test]
    fn devices_json_is_always_an_array() {
        let json = devices_json();
        assert!(json.starts_with('[') && json.ends_with(']'));
    }
}
