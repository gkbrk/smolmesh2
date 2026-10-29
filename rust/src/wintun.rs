use libc::{c_ulonglong, c_void, wchar_t};
use libloading::Library;

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

type WintunCreateAdapter = unsafe extern "C" fn(*const libc::wchar_t, *const wchar_t, *const c_void) -> *const c_void;

type WintunGetAdapterLuid = unsafe extern "C" fn(*const c_void, *mut c_ulonglong);

type WintunStartSession = unsafe extern "C" fn(*const c_void, i32) -> *const c_void;

type WintunGetReadWaitEvent = unsafe extern "C" fn(*const c_void) -> windows_sys::Win32::Foundation::HANDLE;

type WintunReceivePacket = unsafe extern "C" fn(*const c_void, *mut i32) -> *const c_void;

type WintunReleaseReceivePacket = unsafe extern "C" fn(*const c_void, *const c_void);

type WintunAllocateSendPacket = unsafe extern "C" fn(*const c_void, i32) -> *mut u8;

type WintunSendPacket = unsafe extern "C" fn(*const c_void, *const u8);

pub struct WintunAdapterHandle(*const libc::c_void);
unsafe impl Send for WintunAdapterHandle {}

#[derive(Clone)]
pub struct WintunSessionHandle(*const libc::c_void);
unsafe impl Send for WintunSessionHandle {}
unsafe impl Sync for WintunSessionHandle {}

pub struct WintunDLL {
  _lib: Library,
  sym_wintun_create_adapter: WintunCreateAdapter,
  sym_wintun_get_adapter_luid: WintunGetAdapterLuid,
  sym_wintun_start_session: WintunStartSession,
  sym_wintun_get_read_wait_event: WintunGetReadWaitEvent,
  sym_wintun_receive_packet: WintunReceivePacket,
  sym_wintun_release_receive_packet: WintunReleaseReceivePacket,
  sym_wintun_allocate_send_packet: WintunAllocateSendPacket,
  sym_wintun_send_packet: WintunSendPacket,
}

fn str_utf16_cstr(s: &str) -> Vec<u16> {
  let mut res = Vec::with_capacity(s.len() + 1);
  res.extend(s.encode_utf16());
  res.push(0);
  res
}

impl WintunDLL {
  pub fn new(path: &str) -> Result<Self> {
    unsafe {
      let lib = Library::new(path)?;
      let wintun_create_adapter = *lib.get(b"WintunCreateAdapter")?;
      let wintun_get_adapter_luid = *lib.get(b"WintunGetAdapterLUID")?;
      let wintun_start_session = *lib.get(b"WintunStartSession")?;
      let wintun_get_read_wait_event = *lib.get(b"WintunGetReadWaitEvent")?;
      let wintun_receive_packet = *lib.get(b"WintunReceivePacket")?;
      let wintun_release_receive_packet = *lib.get(b"WintunReleaseReceivePacket")?;
      let wintun_allocate_send_packet = *lib.get(b"WintunAllocateSendPacket")?;
      let wintun_send_packet = *lib.get(b"WintunSendPacket")?;

      Ok(Self {
        _lib: lib,
        sym_wintun_create_adapter: wintun_create_adapter,
        sym_wintun_get_adapter_luid: wintun_get_adapter_luid,
        sym_wintun_start_session: wintun_start_session,
        sym_wintun_get_read_wait_event: wintun_get_read_wait_event,
        sym_wintun_receive_packet: wintun_receive_packet,
        sym_wintun_release_receive_packet: wintun_release_receive_packet,
        sym_wintun_allocate_send_packet: wintun_allocate_send_packet,
        sym_wintun_send_packet: wintun_send_packet,
      })
    }
  }

  pub fn wintun_create_adapter(&self, name: &str) -> Result<WintunAdapterHandle> {
    unsafe {
      let adapter = (self.sym_wintun_create_adapter)(
        str_utf16_cstr(name).as_ptr() as *const libc::wchar_t,
        str_utf16_cstr("Wintun").as_ptr() as *const libc::wchar_t,
        std::ptr::null(),
      );

      if adapter.is_null() {
        let err_code = windows_sys::Win32::Foundation::GetLastError();
        let err = format!("WintunCreateAdapter failed with error code {}", err_code);
        return Err(err.into());
      }

      Ok(WintunAdapterHandle(adapter))
    }
  }

  pub fn wintun_get_adapter_luid(&self, adapter: &WintunAdapterHandle) -> u64 {
    unsafe {
      let mut luid = 0u64;
      (self.sym_wintun_get_adapter_luid)(adapter.0, &mut luid);
      luid
    }
  }

  pub fn wintun_start_session(&self, adapter: &WintunAdapterHandle, capacity: i32) -> Result<WintunSessionHandle> {
    unsafe {
      let session = (self.sym_wintun_start_session)(adapter.0, capacity);

      if session.is_null() {
        let err_code = windows_sys::Win32::Foundation::GetLastError();
        let err = format!("WintunStartSession failed with error code {}", err_code);
        return Err(err.into());
      }

      Ok(WintunSessionHandle(session))
    }
  }

  pub fn wintun_get_read_wait_event(&self, session: &WintunSessionHandle) -> windows_sys::Win32::Foundation::HANDLE {
    unsafe { (self.sym_wintun_get_read_wait_event)(session.0) }
  }

  pub fn wintun_receive_packet(&self, session: &WintunSessionHandle) -> Result<Vec<u8>> {
    unsafe {
      let mut packet_size = 0i32;
      let packet = (self.sym_wintun_receive_packet)(session.0, &mut packet_size);

      if packet.is_null() {
        let err_code = windows_sys::Win32::Foundation::GetLastError();
        let err = format!("WintunReceivePacket failed with error code {}", err_code);
        return Err(err.into());
      }

      let buf = std::slice::from_raw_parts(packet as *const u8, packet_size as usize).to_vec();

      (self.sym_wintun_release_receive_packet)(session.0, packet);

      Ok(buf)
    }
  }

  pub fn wintun_send_packet(&self, session: &WintunSessionHandle, data: &[u8]) -> Result<()> {
    unsafe {
      let packet = (self.sym_wintun_allocate_send_packet)(session.0, data.len() as i32);

      if packet.is_null() {
        let error_code = windows_sys::Win32::Foundation::GetLastError();
        let error = format!("WintunAllocateSendPacket failed with error code {}", error_code);
        return Err(error.into());
      }

      std::ptr::copy_nonoverlapping(data.as_ptr(), packet, data.len());

      (self.sym_wintun_send_packet)(session.0, packet);

      Ok(())
    }
  }
}
