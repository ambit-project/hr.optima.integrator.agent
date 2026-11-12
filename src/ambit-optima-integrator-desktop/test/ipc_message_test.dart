import 'dart:convert';
import 'dart:typed_data';

import 'package:ambit_optima_integrator_desktop/ipc/ipc_message.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('a request is framed the way the Worker reads it', () {
    final frame = encodeRequest('status');

    final length = decodeLength(Uint8List.sublistView(frame, 0, 4));
    expect(length, frame.length - 4);
    expect(jsonDecode(utf8.decode(frame.sublist(4))), {
      'version': 1,
      'command': 'status',
    });
  });

  for (final length in [0, -1, ipcMaxMessageBytes + 1]) {
    test('length $length is refused', () {
      final header = Uint8List(4);
      ByteData.sublistView(header).setInt32(0, length, Endian.little);

      expect(() => decodeLength(header), throwsFormatException);
    });
  }

  test('a response is read from the camelCase JSON', () {
    final response = decodeResponse(
      utf8.encode('{"ok":false,"error":"unknown_command"}'),
    );

    expect(response.ok, isFalse);
    expect(response.error, 'unknown_command');
  });
}
