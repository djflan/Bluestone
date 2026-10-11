//! Loading plugin binaries: VST3 bundles in each platform's layout, and CLAP files or bundles.

use std::ffi::c_void;
use std::path::{Path, PathBuf};

pub struct Library {
    #[cfg(target_os = "macos")]
    bundle: Option<macos::Bundle>,
    plain: Option<libloading::Library>,
    raw_handle: *mut c_void,
}

impl Library {
    /// Loads a VST3 module: on macOS the bundle, elsewhere the binary inside it (or a single-file module).
    pub fn open_vst3(path: &Path) -> Result<Library, String> {
        #[cfg(target_os = "macos")]
        {
            if path.is_dir() {
                return Library::open_bundle(path);
            }
        }
        let binary = vst3_binary(path)?;
        Library::open_plain(&binary)
    }

    /// Loads a CLAP module: a bundle on macOS, a plain shared library elsewhere.
    pub fn open_clap(path: &Path) -> Result<Library, String> {
        #[cfg(target_os = "macos")]
        {
            if path.is_dir() {
                return Library::open_bundle(path);
            }
        }
        Library::open_plain(path)
    }

    #[cfg(target_os = "macos")]
    fn open_bundle(path: &Path) -> Result<Library, String> {
        let bundle = macos::Bundle::open(path)?;
        let raw = bundle.as_ptr();
        Ok(Library {
            bundle: Some(bundle),
            plain: None,
            raw_handle: raw,
        })
    }

    fn open_plain(path: &Path) -> Result<Library, String> {
        if !path.is_file() {
            return Err(format!(
                "The plugin binary {} does not exist.",
                path.display()
            ));
        }
        #[cfg(unix)]
        {
            use libloading::os::unix::{Library as UnixLibrary, RTLD_LAZY, RTLD_LOCAL};
            // SAFETY: loading a plugin runs its initializers; that is what a plugin host does, in a worker process.
            let library = unsafe { UnixLibrary::open(Some(path), RTLD_LAZY | RTLD_LOCAL) }
                .map_err(|e| format!("Could not load {}: {e}", path.display()))?;
            let raw = library.into_raw();
            // SAFETY: `raw` came from `into_raw` just above.
            let library = unsafe { UnixLibrary::from_raw(raw) };
            Ok(Library {
                #[cfg(target_os = "macos")]
                bundle: None,
                plain: Some(library.into()),
                raw_handle: raw,
            })
        }
        #[cfg(windows)]
        {
            // SAFETY: as above.
            let library = unsafe { libloading::Library::new(path) }
                .map_err(|e| format!("Could not load {}: {e}", path.display()))?;
            Ok(Library {
                plain: Some(library),
                raw_handle: std::ptr::null_mut(),
            })
        }
    }

    /// A function or data symbol, or None.
    pub fn symbol(&self, name: &str) -> Option<*mut c_void> {
        #[cfg(target_os = "macos")]
        if let Some(bundle) = &self.bundle {
            return bundle.symbol(name);
        }
        let library = self.plain.as_ref()?;
        let mut bytes = name.as_bytes().to_vec();
        bytes.push(0);
        // SAFETY: the symbol is only read as an address.
        unsafe {
            library
                .get::<*mut c_void>(bytes.as_slice())
                .ok()
                .map(|s| *s)
        }
    }

    /// CFBundleRef on macOS, the dlopen handle on Linux; what VST3's module entry functions take.
    pub fn raw_handle(&self) -> *mut c_void {
        self.raw_handle
    }
}

/// The binary inside a VST3 bundle on Windows and Linux (`Contents/<arch>-win/<name>.vst3`,
/// `Contents/<arch>-linux/<name>.so`), or the path itself for a single-file module.
pub fn vst3_binary(path: &Path) -> Result<PathBuf, String> {
    if path.is_file() {
        return Ok(path.to_path_buf());
    }
    let stem = path
        .file_stem()
        .and_then(|s| s.to_str())
        .ok_or("The bundle has no name.")?;
    let contents = path.join("Contents");
    let candidates: Vec<PathBuf> = if cfg!(windows) {
        architecture_folders()
            .iter()
            .map(|a| {
                contents
                    .join(format!("{a}-win"))
                    .join(format!("{stem}.vst3"))
            })
            .collect()
    } else {
        architecture_folders()
            .iter()
            .map(|a| {
                contents
                    .join(format!("{a}-linux"))
                    .join(format!("{stem}.so"))
            })
            .collect()
    };
    candidates
        .iter()
        .find(|c| c.is_file())
        .cloned()
        .ok_or_else(|| {
            format!(
                "The bundle has no binary for this architecture (looked for {}).",
                candidates
                    .first()
                    .map(|c| c.display().to_string())
                    .unwrap_or_default()
            )
        })
}

fn architecture_folders() -> &'static [&'static str] {
    if cfg!(target_arch = "x86_64") {
        &["x86_64"]
    } else if cfg!(all(target_arch = "aarch64", windows)) {
        &["arm64", "arm64ec", "arm64x"]
    } else if cfg!(target_arch = "aarch64") {
        &["aarch64"]
    } else {
        &["i386", "x86"]
    }
}

#[cfg(target_os = "macos")]
mod macos {
    use std::ffi::c_void;
    use std::os::unix::ffi::OsStrExt;
    use std::path::Path;

    type CFTypeRef = *const c_void;
    type CFIndex = isize;

    #[link(name = "CoreFoundation", kind = "framework")]
    unsafe extern "C" {
        fn CFURLCreateFromFileSystemRepresentation(
            allocator: CFTypeRef,
            buffer: *const u8,
            length: CFIndex,
            is_directory: u8,
        ) -> CFTypeRef;
        fn CFBundleCreate(allocator: CFTypeRef, url: CFTypeRef) -> CFTypeRef;
        fn CFBundleLoadExecutableAndReturnError(bundle: CFTypeRef, error: *mut CFTypeRef) -> u8;
        fn CFBundleGetFunctionPointerForName(bundle: CFTypeRef, name: CFTypeRef) -> *mut c_void;
        fn CFBundleGetDataPointerForName(bundle: CFTypeRef, name: CFTypeRef) -> *mut c_void;
        fn CFStringCreateWithBytes(
            allocator: CFTypeRef,
            bytes: *const u8,
            length: CFIndex,
            encoding: u32,
            external: u8,
        ) -> CFTypeRef;
        fn CFErrorCopyDescription(error: CFTypeRef) -> CFTypeRef;
        fn CFStringGetCString(
            string: CFTypeRef,
            buffer: *mut u8,
            size: CFIndex,
            encoding: u32,
        ) -> u8;
        fn CFRelease(value: CFTypeRef);
    }

    const UTF8: u32 = 0x0800_0100;

    pub struct Bundle(CFTypeRef);

    impl Bundle {
        pub fn open(path: &Path) -> Result<Bundle, String> {
            let bytes = path.as_os_str().as_bytes();
            // SAFETY: plain CoreFoundation calls with valid arguments; every created object is released.
            unsafe {
                let url = CFURLCreateFromFileSystemRepresentation(
                    std::ptr::null(),
                    bytes.as_ptr(),
                    bytes.len() as CFIndex,
                    1,
                );
                if url.is_null() {
                    return Err(format!("Not a valid bundle path: {}", path.display()));
                }
                let bundle = CFBundleCreate(std::ptr::null(), url);
                CFRelease(url);
                if bundle.is_null() {
                    return Err(format!("Not a bundle: {}", path.display()));
                }
                let mut error: CFTypeRef = std::ptr::null();
                if CFBundleLoadExecutableAndReturnError(bundle, &mut error) == 0 {
                    let message = describe(error);
                    if !error.is_null() {
                        CFRelease(error);
                    }
                    CFRelease(bundle);
                    return Err(format!("Could not load {}: {message}", path.display()));
                }
                Ok(Bundle(bundle))
            }
        }

        pub fn as_ptr(&self) -> *mut c_void {
            self.0 as *mut c_void
        }

        pub fn symbol(&self, name: &str) -> Option<*mut c_void> {
            // SAFETY: as above.
            unsafe {
                let string = CFStringCreateWithBytes(
                    std::ptr::null(),
                    name.as_ptr(),
                    name.len() as CFIndex,
                    UTF8,
                    0,
                );
                if string.is_null() {
                    return None;
                }
                let mut pointer = CFBundleGetFunctionPointerForName(self.0, string);
                if pointer.is_null() {
                    pointer = CFBundleGetDataPointerForName(self.0, string);
                }
                CFRelease(string);
                (!pointer.is_null()).then_some(pointer)
            }
        }
    }

    impl Drop for Bundle {
        fn drop(&mut self) {
            // The executable stays loaded: unloading plugin code that may have left threads or atexit handlers
            // behind is not safe, and the worker process ends soon after anyway.
            // SAFETY: the bundle was created by CFBundleCreate.
            unsafe { CFRelease(self.0) };
        }
    }

    unsafe fn describe(error: CFTypeRef) -> String {
        if error.is_null() {
            return "unknown error".to_string();
        }
        // SAFETY: `error` is a CFErrorRef.
        unsafe {
            let description = CFErrorCopyDescription(error);
            if description.is_null() {
                return "unknown error".to_string();
            }
            let mut buffer = vec![0u8; 1024];
            let ok = CFStringGetCString(
                description,
                buffer.as_mut_ptr(),
                buffer.len() as CFIndex,
                UTF8,
            );
            CFRelease(description);
            if ok == 0 {
                return "unknown error".to_string();
            }
            let len = buffer.iter().position(|&b| b == 0).unwrap_or(buffer.len());
            String::from_utf8_lossy(&buffer[..len]).into_owned()
        }
    }
}
