import 'dart:io';

import 'package:ambit_optima_integrator_desktop/ipc/windows_pipe_transport.dart';
import 'package:flutter_test/flutter_test.dart';

/// The stage 0 proof for Dart: runs only where the Worker is installed as a service.
void main() {
  final expected = Platform.environment['AMBIT_SPIKE_EXPECTED_LOGIN'];

  test('Dart reads the service identity through the named pipe', () async {
    final response = await sendOverWindowsPipe('sql.identity');

    expect(response.ok, isTrue, reason: response.error);
    expect(response.data!['loginName'], expected);
  }, skip: expected == null ? 'No Worker service on this machine.' : false);
}
