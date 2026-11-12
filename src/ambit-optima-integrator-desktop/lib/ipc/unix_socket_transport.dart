import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'agent_client.dart';
import 'ipc_message.dart';

String defaultSocketPath() {
  final dir =
      Platform.environment['XDG_RUNTIME_DIR'] ?? Directory.systemTemp.path;
  return '$dir/ambit-optima-integrator.sock';
}

/// Development on Linux. The Worker's Windows pipe has the same frames.
Future<IpcResponse> sendOverUnixSocket(String command, {String? path}) async {
  final Socket socket;
  try {
    socket = await Socket.connect(
      InternetAddress(
        path ?? defaultSocketPath(),
        type: InternetAddressType.unix,
      ),
      0,
    );
  } on SocketException catch (e) {
    throw WorkerUnavailable(e.message);
  }

  try {
    socket.add(encodeRequest(command));
    await socket.flush();

    final bytes = BytesBuilder(copy: false);
    int? length;
    await for (final chunk in socket) {
      bytes.add(chunk);
      if (length == null && bytes.length >= 4) {
        length = decodeLength(Uint8List.sublistView(bytes.toBytes(), 0, 4));
      }
      if (length != null && bytes.length >= 4 + length) {
        return decodeResponse(
          Uint8List.sublistView(bytes.toBytes(), 4, 4 + length),
        );
      }
    }
    throw const WorkerUnavailable('The Worker closed the connection.');
  } finally {
    socket.destroy();
  }
}
