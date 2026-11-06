import 'dart:ffi';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:ffi/ffi.dart';
import 'package:win32/win32.dart';

import 'agent_client.dart';
import 'ipc_message.dart';

const String defaultPipeName = r'\\.\pipe\ambit-optima-agent';

/// dart:io has no named pipes, so this calls Win32 directly. The calls block, hence the isolate:
/// a slow Worker must never freeze the window.
Future<IpcResponse> sendOverWindowsPipe(String command, {String? pipeName}) =>
    Isolate.run(() => _exchange(command, pipeName ?? defaultPipeName));

IpcResponse _exchange(String command, String pipeName) => using((arena) {
  final handle = _open(pipeName, arena);
  try {
    _writeAll(handle, encodeRequest(command), arena);
    final length = decodeLength(_readExactly(handle, 4, arena));
    return decodeResponse(_readExactly(handle, length, arena));
  } finally {
    CloseHandle(handle);
  }
});

HANDLE _open(String pipeName, Arena arena) {
  const attempts = 20;
  for (var attempt = 0; attempt < attempts; attempt++) {
    final Win32Result(value: handle, :error) = CreateFile(
      arena.pcwstr(pipeName),
      GENERIC_READ | GENERIC_WRITE,
      FILE_SHARE_NONE,
      null,
      OPEN_EXISTING,
      // Identification only: whoever holds this pipe's name can learn who we are, but cannot act as us.
      FILE_ATTRIBUTE_NORMAL | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
      null,
    );
    if (handle.isValid) return handle;
    if (error != ERROR_PIPE_BUSY) {
      throw WorkerUnavailable('Win32 error $error');
    }
    Sleep(50);
  }
  throw const WorkerUnavailable('The Worker is busy.');
}

void _writeAll(HANDLE handle, Uint8List bytes, Arena arena) {
  final buffer = arena<Uint8>(bytes.length);
  buffer.asTypedList(bytes.length).setAll(0, bytes);
  final written = arena<Uint32>();
  var offset = 0;
  while (offset < bytes.length) {
    final Win32Result(value: ok, :error) = WriteFile(
      handle,
      buffer + offset,
      bytes.length - offset,
      written,
      null,
    );
    if (!ok) {
      throw WorkerUnavailable('Win32 error $error');
    }
    offset += written.value;
  }
}

Uint8List _readExactly(HANDLE handle, int count, Arena arena) {
  final buffer = arena<Uint8>(count);
  final read = arena<Uint32>();
  var offset = 0;
  while (offset < count) {
    final Win32Result(value: ok, :error) = ReadFile(
      handle,
      buffer + offset,
      count - offset,
      read,
      null,
    );
    if (!ok || read.value == 0) {
      throw WorkerUnavailable('Win32 error $error');
    }
    offset += read.value;
  }
  return Uint8List.fromList(buffer.asTypedList(count));
}
