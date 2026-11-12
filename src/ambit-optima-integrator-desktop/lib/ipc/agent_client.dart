import 'dart:io';

import 'ipc_message.dart';
import 'unix_socket_transport.dart';
import 'windows_pipe_transport.dart';

/// No Worker on the other end: the service is stopped or not installed.
class WorkerUnavailable implements Exception {
  const WorkerUnavailable(this.reason);

  final String reason;

  @override
  String toString() => 'WorkerUnavailable: $reason';
}

/// Sends one command to the Worker and returns its answer.
typedef IpcTransport = Future<IpcResponse> Function(String command);

class AgentClient {
  AgentClient({IpcTransport? transport})
    : _send =
          transport ??
          (Platform.isWindows ? sendOverWindowsPipe : sendOverUnixSocket);

  final IpcTransport _send;

  Future<IpcResponse> status() => _send('status');

  Future<IpcResponse> sqlIdentity() => _send('sql.identity');
}
