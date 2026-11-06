#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include "flutter_window.h"
#include "utils.h"

namespace {

constexpr wchar_t kWindowTitle[] = L"Ambit Optima Agent";
constexpr wchar_t kWindowClass[] = L"FLUTTER_RUNNER_WIN32_WINDOW";

}  // namespace

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  // One copy per session: starting it again brings back the window, which may be hidden in the tray.
  HANDLE instance_lock =
      ::CreateMutex(nullptr, FALSE, L"Local\\AmbitOptimaAgentDesktop");
  if (instance_lock && ::GetLastError() == ERROR_ALREADY_EXISTS) {
    if (HWND running = ::FindWindow(kWindowClass, kWindowTitle)) {
      ::AllowSetForegroundWindow(ASFW_ANY);
      ::PostMessage(running, ::RegisterWindowMessage(kShowWindowMessage), 0, 0);
    }
    return EXIT_SUCCESS;
  }

  // Attach to console when present (e.g., 'flutter run') or create a
  // new console when running with a debugger.
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  // Initialize COM, so that it is available for use in the library and/or
  // plugins.
  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

  flutter::DartProject project(L"data");

  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  const double scale = FlutterDesktopGetDpiForMonitor(::MonitorFromPoint({0, 0}, MONITOR_DEFAULTTOPRIMARY)) / 96.0;
  const int width = 760;
  const int height = 640;

  FlutterWindow window(project);
  Win32Window::Point origin(
      static_cast<unsigned int>((::GetSystemMetrics(SM_CXSCREEN) / scale - width) / 2),
      static_cast<unsigned int>((::GetSystemMetrics(SM_CYSCREEN) / scale - height) / 2));
  Win32Window::Size size(width, height);
  if (!window.Create(kWindowTitle, origin, size)) {
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  ::CoUninitialize();
  return EXIT_SUCCESS;
}
