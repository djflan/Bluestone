use cpal::traits::DeviceTrait;
use std::collections::HashMap;
use std::str::FromStr;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Mutex, OnceLock};

#[derive(Default)]
struct AsioCache {
    devices: HashMap<(bool, String), cpal::Device>,
    defaults: HashMap<bool, String>,
}

static ASIO_CACHE: OnceLock<Mutex<AsioCache>> = OnceLock::new();
static ASIO_STREAMS: AtomicUsize = AtomicUsize::new(0);

pub(crate) fn remember_asio_devices(input: bool, devices: &[cpal::Device]) {
    let mut cache = ASIO_CACHE.get_or_init(Default::default).lock().unwrap();
    cache
        .devices
        .retain(|(direction, _), _| *direction != input);
    cache.defaults.remove(&input);
    let mut first = None;
    for device in devices {
        let Ok(id) = device.id() else { continue };
        let id = id.to_string();
        first.get_or_insert_with(|| id.clone());
        cache.devices.insert((input, id), device.clone());
    }
    if let Some(first) = first {
        cache.defaults.insert(input, first);
    }
}

pub(crate) fn cached_asio_devices(input: bool) -> Option<Vec<cpal::Device>> {
    let cache = ASIO_CACHE.get()?.lock().ok()?;
    let mut devices = cache
        .devices
        .iter()
        .filter(|((direction, _), _)| *direction == input)
        .map(|(_, device)| device.clone())
        .collect::<Vec<_>>();
    devices.sort_by_key(|device| device.id().map(|id| id.to_string()).unwrap_or_default());
    (!devices.is_empty()).then_some(devices)
}

pub(crate) fn cached_asio_device(id: &str, input: bool) -> Option<cpal::Device> {
    ASIO_CACHE
        .get()?
        .lock()
        .ok()?
        .devices
        .get(&(input, id.to_string()))
        .cloned()
}

pub(crate) fn cached_asio_default(input: bool) -> Option<cpal::Device> {
    let cache = ASIO_CACHE.get()?.lock().ok()?;
    let id = cache.defaults.get(&input)?;
    cache.devices.get(&(input, id.clone())).cloned()
}

pub(crate) fn asio_stream_opened() {
    ASIO_STREAMS.fetch_add(1, Ordering::AcqRel);
}

pub(crate) fn asio_stream_closed() {
    ASIO_STREAMS.fetch_sub(1, Ordering::AcqRel);
}

pub(crate) fn asio_stream_is_active() -> bool {
    ASIO_STREAMS.load(Ordering::Acquire) > 0
}

pub(crate) fn resolve_sample_rate(
    requested: u32,
    default: u32,
    channels: u32,
    ranges: &[(u32, u32, u32)],
) -> Result<u32, String> {
    if requested > 0 {
        return Ok(requested);
    }
    if default > 0 {
        return Ok(default);
    }

    for rate in [
        48_000, 44_100, 96_000, 88_200, 192_000, 176_400, 32_000, 22_050, 16_000, 11_025, 8_000,
    ] {
        if ranges.iter().any(|&(min, max, supported_channels)| {
            supported_channels == channels && min <= rate && rate <= max
        }) {
            return Ok(rate);
        }
    }

    ranges
        .iter()
        .find(|&&(_, _, supported_channels)| supported_channels == channels)
        .map(|&(min, _, _)| min)
        .ok_or_else(|| format!("The device has no configuration with {channels} channels."))
}

pub(crate) struct AsioStreamGuard(bool);

impl AsioStreamGuard {
    pub(crate) fn new(host_name: Option<&str>) -> Self {
        let is_asio = host_name.is_some_and(|name| name.eq_ignore_ascii_case("ASIO"));
        if is_asio {
            asio_stream_opened();
        }
        Self(is_asio)
    }
}

impl Drop for AsioStreamGuard {
    fn drop(&mut self) {
        if self.0 {
            asio_stream_closed();
        }
    }
}

pub(crate) fn select(name: Option<&str>) -> Result<cpal::Host, String> {
    let Some(name) = name.filter(|name| !name.is_empty()) else {
        return Ok(cpal::default_host());
    };
    let id = cpal::HostId::from_str(name)
        .map_err(|_| format!("The audio host {name} is not available in this build."))?;
    cpal::host_from_id(id)
        .map_err(|error| format!("The audio host {name} could not be opened: {error}"))
}

pub(crate) fn names_json() -> String {
    let mut json = String::from("[");
    for (index, id) in cpal::available_hosts().iter().enumerate() {
        if index > 0 {
            json.push(',');
        }
        crate::json::string(&mut json, id.name());
    }
    json.push(']');
    json
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unknown_default_rate_prefers_48_khz_when_supported_for_the_channels() {
        let rates = [
            (44_100, 44_100, 2),
            (96_000, 96_000, 2),
            (48_000, 48_000, 2),
        ];

        assert_eq!(resolve_sample_rate(0, 0, 2, &rates).unwrap(), 48_000);
    }

    #[test]
    fn unknown_default_rate_uses_the_first_supported_channel_range() {
        let rates = [(44_100, 44_100, 1), (96_000, 96_000, 2)];

        assert_eq!(resolve_sample_rate(0, 0, 2, &rates).unwrap(), 96_000);
    }
}
