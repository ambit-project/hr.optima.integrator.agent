# Ambit Optima Integrator UI

Shows whether the integrator is working. It has no SQL logic and no way of sending SQL: it asks the Worker over IPC.

On Windows the window lives in the notification area, which `windows/runner/flutter_window.cpp` handles natively:

- Closing or minimizing hides the window. The service runs on either way.
- A click on the tray icon opens it, and the tray menu has Otwórz and Zakończ.
- Starting the app again shows the window that is already running.

```bash
flutter test
flutter run -d windows
```
