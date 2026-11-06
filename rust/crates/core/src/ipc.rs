//! Frame: 4-byte little-endian length + UTF-8 JSON (ARCHITECTURE section 3).
pub const MAX_REQUEST: usize = 64 * 1024;
pub const MAX_REPLY: usize = 4 * 1024 * 1024;
pub const DEFAULT_PIPE: &str = r"\\.\pipe\DellFans.Control.v2";

#[derive(Debug, PartialEq, Eq)]
pub enum FrameError { TooLarge(usize) }

pub fn encode_frame(payload: &[u8]) -> Result<Vec<u8>, FrameError> { encode_frame_max(payload, MAX_REQUEST) }

pub fn encode_frame_max(payload: &[u8], max: usize) -> Result<Vec<u8>, FrameError> {
    if payload.len() > max { return Err(FrameError::TooLarge(payload.len())); }
    let mut v = Vec::with_capacity(4 + payload.len());
    v.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    v.extend_from_slice(payload);
    Ok(v)
}

/// Validates a received header against `max`.
pub fn frame_len(header: [u8; 4], max: usize) -> Result<usize, FrameError> {
    let n = u32::from_le_bytes(header) as usize;
    if n > max { Err(FrameError::TooLarge(n)) } else { Ok(n) }
}

/// Splits one frame off the front of `buf`: Ok(None) = incomplete, Ok(Some((payload, consumed))).
pub fn decode_frame(buf: &[u8], max: usize) -> Result<Option<(&[u8], usize)>, FrameError> {
    if buf.len() < 4 { return Ok(None); }
    let n = frame_len([buf[0], buf[1], buf[2], buf[3]], max)?;
    if buf.len() < 4 + n { return Ok(None); }
    Ok(Some((&buf[4..4 + n], 4 + n)))
}

/// `DELLFANS_PIPE` may be a bare name or a full path. 
pub fn pipe_path(env: Option<&str>) -> String {
    match env {
        None | Some("") => DEFAULT_PIPE.to_string(),
        Some(n) if n.starts_with(r"\\") => n.to_string(),
        Some(n) => format!(r"\\.\pipe\{n}"),
    }
}

pub fn status_request() -> &'static str { r#"{"Command":"Status","Detail":false}"# }
/// Asks the background controller to hand the fan back to the BIOS and exit.
pub fn exit_request() -> &'static str { r#"{"Command":"Exit"}"# }

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn roundtrip() {
        let f = encode_frame(b"{\"a\":1}").unwrap();
        assert_eq!(&f[..4], &[7, 0, 0, 0]);
        let (p, used) = decode_frame(&f, MAX_REPLY).unwrap().unwrap();
        assert_eq!(p, b"{\"a\":1}");
        assert_eq!(used, f.len());
    }
    #[test]
    fn incomplete_and_two_frames() {
        let mut f = encode_frame(b"abc").unwrap();
        assert_eq!(decode_frame(&f[..2], MAX_REPLY).unwrap(), None);
        assert_eq!(decode_frame(&f[..6], MAX_REPLY).unwrap(), None);
        f.extend(encode_frame(b"de").unwrap());
        let (p, used) = decode_frame(&f, MAX_REPLY).unwrap().unwrap();
        assert_eq!((p, used), (&b"abc"[..], 7));
        assert_eq!(decode_frame(&f[used..], MAX_REPLY).unwrap().unwrap().0, b"de");
    }
    #[test]
    fn limits() {
        assert!(encode_frame(&vec![0; MAX_REQUEST + 1]).is_err());
        assert!(encode_frame(&vec![0; MAX_REQUEST]).is_ok());
        assert_eq!(frame_len((MAX_REPLY as u32 + 1).to_le_bytes(), MAX_REPLY), Err(FrameError::TooLarge(MAX_REPLY + 1)));
        assert_eq!(frame_len((MAX_REPLY as u32).to_le_bytes(), MAX_REPLY), Ok(MAX_REPLY));
    }
    #[test]
    fn pipe_names() {
        assert_eq!(pipe_path(None), DEFAULT_PIPE);
        assert_eq!(pipe_path(Some("T1")), r"\\.\pipe\T1");
        assert_eq!(pipe_path(Some(r"\\.\pipe\X")), r"\\.\pipe\X");
    }
    #[test]
    fn status_req_is_summary() {
        let v: serde_json::Value = serde_json::from_str(status_request()).unwrap();
        assert_eq!(v["Command"], "Status");
        assert_eq!(v["Detail"], false);
    }
}
