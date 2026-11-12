import 'dart:convert';
import 'dart:typed_data';

const int ipcProtocolVersion = 1;
const int ipcMaxMessageBytes = 1024 * 1024;

/// The Worker's answer. The UI sends a command's name only and has no way of sending SQL.
class IpcResponse {
  const IpcResponse({required this.ok, this.data, this.error});

  factory IpcResponse.fromJson(Map<String, dynamic> json) => IpcResponse(
    ok: json['ok'] as bool,
    data: json['data'] as Map<String, dynamic>?,
    error: json['error'] as String?,
  );

  final bool ok;
  final Map<String, dynamic>? data;
  final String? error;
}

/// A 4-byte little-endian length, then UTF-8 JSON: the same frame as IpcFraming in the Worker.
Uint8List encodeRequest(String command) {
  final body = utf8.encode(
    jsonEncode({'version': ipcProtocolVersion, 'command': command}),
  );
  final frame = Uint8List(4 + body.length);
  ByteData.sublistView(frame).setInt32(0, body.length, Endian.little);
  frame.setRange(4, frame.length, body);
  return frame;
}

int decodeLength(Uint8List header) {
  final length = ByteData.sublistView(header).getInt32(0, Endian.little);
  if (length <= 0 || length > ipcMaxMessageBytes) {
    throw const FormatException('The IPC message has an invalid length.');
  }
  return length;
}

IpcResponse decodeResponse(Uint8List body) =>
    IpcResponse.fromJson(jsonDecode(utf8.decode(body)) as Map<String, dynamic>);
