#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <shellapi.h>

#include <memory>

#include "win32_window.h"

// Sent by a second instance of the app, to bring this window back.
inline constexpr wchar_t kShowWindowMessage[] = L"AmbitOptimaIntegratorShowWindow";

// A window that hosts a Flutter view and lives in the notification area.
class FlutterWindow : public Win32Window {
 public:
  // Creates a new FlutterWindow hosting a Flutter view running |project|.
  explicit FlutterWindow(const flutter::DartProject& project);
  virtual ~FlutterWindow();

 protected:
  // Win32Window:
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  void AddTrayIcon(HWND window);
  void HideToTray(HWND window);
  void ShowFromTray(HWND window);
  void ShowTrayMenu(HWND window);

  // The project to run.
  flutter::DartProject project_;

  // The Flutter instance hosted by this window.
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;

  NOTIFYICONDATA tray_icon_{};
  UINT taskbar_created_message_ = 0;
  UINT show_window_message_ = 0;
  bool told_about_tray_ = false;
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
