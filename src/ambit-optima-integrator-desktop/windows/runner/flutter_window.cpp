#include "flutter_window.h"

#include <optional>

#include "flutter/generated_plugin_registrant.h"
#include "resource.h"

namespace {

constexpr UINT kTrayMessage = WM_APP + 1;
constexpr UINT kTrayIconId = 1;
constexpr UINT_PTR kMenuOpen = 1;
constexpr UINT_PTR kMenuQuit = 2;
constexpr int kMinimumWidth = 560;
constexpr int kMinimumHeight = 480;

}  // namespace

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  taskbar_created_message_ = ::RegisterWindowMessage(L"TaskbarCreated");
  show_window_message_ = ::RegisterWindowMessage(kShowWindowMessage);
  AddTrayIcon(GetHandle());

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  ::Shell_NotifyIcon(NIM_DELETE, &tray_icon_);

  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  // Registered messages have no fixed value, so they cannot be switch cases.
  if (message == show_window_message_ && message != 0) {
    ShowFromTray(hwnd);
    return 0;
  }
  // Explorer restarted and forgot every tray icon.
  if (message == taskbar_created_message_ && message != 0) {
    ::Shell_NotifyIcon(NIM_ADD, &tray_icon_);
    return 0;
  }

  switch (message) {
    case WM_CLOSE:
      HideToTray(hwnd);
      return 0;

    case WM_SYSCOMMAND:
      if ((wparam & 0xFFF0) == SC_MINIMIZE) {
        HideToTray(hwnd);
        return 0;
      }
      break;

    // Windows is shutting down or the session ends: hiding would hold that up.
    case WM_ENDSESSION:
      if (wparam) {
        ::DestroyWindow(hwnd);
      }
      return 0;

    case WM_GETMINMAXINFO: {
      const double scale = FlutterDesktopGetDpiForHWND(hwnd) / 96.0;
      auto* limits = reinterpret_cast<MINMAXINFO*>(lparam);
      limits->ptMinTrackSize.x = static_cast<LONG>(kMinimumWidth * scale);
      limits->ptMinTrackSize.y = static_cast<LONG>(kMinimumHeight * scale);
      return 0;
    }

    case kTrayMessage:
      if (LOWORD(lparam) == WM_LBUTTONUP) {
        ShowFromTray(hwnd);
      } else if (LOWORD(lparam) == WM_RBUTTONUP) {
        ShowTrayMenu(hwnd);
      }
      return 0;
  }

  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}

void FlutterWindow::AddTrayIcon(HWND window) {
  tray_icon_.cbSize = sizeof(tray_icon_);
  tray_icon_.hWnd = window;
  tray_icon_.uID = kTrayIconId;
  tray_icon_.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
  tray_icon_.uCallbackMessage = kTrayMessage;
  tray_icon_.hIcon = static_cast<HICON>(::LoadImage(
      ::GetModuleHandle(nullptr), MAKEINTRESOURCE(IDI_APP_ICON), IMAGE_ICON,
      ::GetSystemMetrics(SM_CXSMICON), ::GetSystemMetrics(SM_CYSMICON),
      LR_DEFAULTCOLOR));
  wcscpy_s(tray_icon_.szTip, L"Ambit Optima Integrator");
  ::Shell_NotifyIcon(NIM_ADD, &tray_icon_);
}

void FlutterWindow::HideToTray(HWND window) {
  ::ShowWindow(window, SW_HIDE);
  if (told_about_tray_) {
    return;
  }
  // Once per run: without it, the app looks closed the first time.
  told_about_tray_ = true;
  NOTIFYICONDATA notice = tray_icon_;
  notice.uFlags = NIF_INFO;
  notice.dwInfoFlags = NIIF_NONE | NIIF_NOSOUND;
  wcscpy_s(notice.szInfoTitle, L"Ambit Optima Integrator");
  wcscpy_s(notice.szInfo,
           L"Aplikacja dzia\u0142a w zasobniku systemowym. "
           L"Kliknij ikon\u0119, aby j\u0105 otworzy\u0107.");
  ::Shell_NotifyIcon(NIM_MODIFY, &notice);
}

void FlutterWindow::ShowFromTray(HWND window) {
  ::ShowWindow(window, ::IsIconic(window) ? SW_RESTORE : SW_SHOW);
  ::SetForegroundWindow(window);
}

void FlutterWindow::ShowTrayMenu(HWND window) {
  HMENU menu = ::CreatePopupMenu();
  ::AppendMenu(menu, MF_STRING, kMenuOpen, L"Otw\u00f3rz");
  ::AppendMenu(menu, MF_SEPARATOR, 0, nullptr);
  ::AppendMenu(menu, MF_STRING, kMenuQuit, L"Zako\u0144cz");
  ::SetMenuDefaultItem(menu, kMenuOpen, FALSE);

  POINT cursor;
  ::GetCursorPos(&cursor);
  // Without the foreground window the menu would not close when clicking away.
  ::SetForegroundWindow(window);
  const UINT choice = ::TrackPopupMenu(
      menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, cursor.x, cursor.y,
      0, window, nullptr);
  ::PostMessage(window, WM_NULL, 0, 0);
  ::DestroyMenu(menu);

  if (choice == kMenuOpen) {
    ShowFromTray(window);
  } else if (choice == kMenuQuit) {
    ::DestroyWindow(window);
  }
}
