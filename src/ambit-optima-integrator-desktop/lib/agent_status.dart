import 'ipc/agent_client.dart';
import 'ipc/ipc_message.dart';

enum AgentHealth { checking, healthy, overprivileged, sqlUnavailable, stopped }

/// What the Worker reports, reduced to what an administrator needs to see.
class AgentStatus {
  const AgentStatus({
    required this.health,
    this.version,
    this.startedAt,
    this.server,
    this.login,
    this.checkedAt,
  });

  static const checking = AgentStatus(health: AgentHealth.checking);

  final AgentHealth health;
  final String? version;
  final DateTime? startedAt;
  final String? server;
  final String? login;
  final DateTime? checkedAt;

  static Future<AgentStatus> read(AgentClient client, DateTime now) async {
    try {
      final worker = await client.status();
      if (!worker.ok) {
        return AgentStatus(health: AgentHealth.stopped, checkedAt: now);
      }
      return _fromAnswers(worker, await client.sqlIdentity(), now);
    } on WorkerUnavailable {
      return AgentStatus(health: AgentHealth.stopped, checkedAt: now);
    }
  }

  static AgentStatus _fromAnswers(
    IpcResponse worker,
    IpcResponse sql,
    DateTime now,
  ) {
    final startedAt = worker.data?['startedAt'] as String?;
    final overprivileged = sql.data?['isOverprivileged'] == true;
    return AgentStatus(
      health: !sql.ok
          ? AgentHealth.sqlUnavailable
          : overprivileged
          ? AgentHealth.overprivileged
          : AgentHealth.healthy,
      version: _shortVersion(worker.data?['version'] as String?),
      startedAt: startedAt == null
          ? null
          : DateTime.tryParse(startedAt)?.toLocal(),
      server: sql.data?['serverName'] as String?,
      login: sql.data?['loginName'] as String?,
      checkedAt: now,
    );
  }

  /// .NET reports 0.2.0.0, while the installer and the rest of the world say 0.2.0.
  static String? _shortVersion(String? version) =>
      version?.replaceFirst(RegExp(r'\.0$'), '');
}
