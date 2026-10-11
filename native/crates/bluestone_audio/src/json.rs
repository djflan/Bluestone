//! A tiny JSON writer for the device list (the only structured data that crosses the ABI as text).

pub(crate) fn string(out: &mut String, value: &str) {
    out.push('"');
    for c in value.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
}

pub(crate) fn numbers(out: &mut String, values: &[u32]) {
    out.push('[');
    for (i, v) in values.iter().enumerate() {
        if i > 0 {
            out.push(',');
        }
        out.push_str(&v.to_string());
    }
    out.push(']');
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn string_escapes_quotes_backslashes_and_controls() {
        let mut out = String::new();
        string(&mut out, "a\"b\\c\nd\u{1}");
        assert_eq!(out, "\"a\\\"b\\\\c\\nd\\u0001\"");
    }

    #[test]
    fn numbers_are_a_json_array() {
        let mut out = String::new();
        numbers(&mut out, &[44100, 48000]);
        assert_eq!(out, "[44100,48000]");
        out.clear();
        numbers(&mut out, &[]);
        assert_eq!(out, "[]");
    }
}
