import 'dart:async';

import 'package:flutter/material.dart';

import 'agent_status.dart';
import 'ipc/agent_client.dart';

/// The one screen of the UI: is the integrator working, and if not, what to do about it.
class StatusPage extends StatefulWidget {
  const StatusPage({
    super.key,
    this.client,
    this.refreshEvery = const Duration(seconds: 10),
  });

  final AgentClient? client;
  final Duration refreshEvery;

  @override
  State<StatusPage> createState() => _StatusPageState();
}

class _StatusPageState extends State<StatusPage> {
  late final AgentClient _client = widget.client ?? AgentClient();
  late final Timer _timer;
  AgentStatus _status = AgentStatus.checking;
  bool _refreshing = false;

  @override
  void initState() {
    super.initState();
    _timer = Timer.periodic(widget.refreshEvery, (_) => _refresh());
    _refresh();
  }

  @override
  void dispose() {
    _timer.cancel();
    super.dispose();
  }

  Future<void> _refresh() async {
    if (_refreshing) return;
    setState(() => _refreshing = true);
    final status = await AgentStatus.read(_client, DateTime.now());
    if (!mounted) return;
    setState(() {
      _status = status;
      _refreshing = false;
    });
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    body: SingleChildScrollView(
      padding: const EdgeInsets.all(28),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 680),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _Header(refreshing: _refreshing, onRefresh: _refresh),
              const SizedBox(height: 24),
              _Summary(health: _status.health),
              const SizedBox(height: 16),
              _Details(
                title: 'Usługa',
                icon: Icons.settings_outlined,
                rows: [
                  ('Stan', _serviceState(_status.health)),
                  ('Wersja', _status.version ?? '—'),
                  ('Działa od', _dateTime(_status.startedAt)),
                ],
              ),
              const SizedBox(height: 16),
              _Details(
                title: 'SQL Server',
                icon: Icons.storage_outlined,
                rows: [
                  ('Połączenie', _sqlState(_status.health)),
                  ('Serwer', _status.server ?? '—'),
                  ('Konto usługi', _status.login ?? '—'),
                ],
              ),
              const SizedBox(height: 20),
              _Footer(
                checkedAt: _status.checkedAt,
                serviceAnswers:
                    _status.health != AgentHealth.stopped &&
                    _status.health != AgentHealth.checking,
              ),
            ],
          ),
        ),
      ),
    ),
  );
}

String _serviceState(AgentHealth health) => switch (health) {
  AgentHealth.checking => 'Sprawdzanie…',
  AgentHealth.stopped => 'Nie odpowiada',
  _ => 'Działa',
};

String _sqlState(AgentHealth health) => switch (health) {
  AgentHealth.checking => 'Sprawdzanie…',
  AgentHealth.stopped => '—',
  AgentHealth.sqlUnavailable => 'Brak połączenia',
  _ => 'Połączono',
};

String _twoDigits(int value) => value.toString().padLeft(2, '0');

String _time(DateTime value) =>
    '${_twoDigits(value.hour)}:${_twoDigits(value.minute)}:${_twoDigits(value.second)}';

String _dateTime(DateTime? value) => value == null
    ? '—'
    : '${_twoDigits(value.day)}.${_twoDigits(value.month)}.${value.year}, '
          '${_twoDigits(value.hour)}:${_twoDigits(value.minute)}';

class _Header extends StatelessWidget {
  const _Header({required this.refreshing, required this.onRefresh});

  final bool refreshing;
  final VoidCallback onRefresh;

  @override
  Widget build(BuildContext context) => Row(
    children: [
      Container(
        width: 36,
        height: 36,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          color: const Color(0xFF0F766E),
          borderRadius: BorderRadius.circular(9),
        ),
        child: const Text(
          'A',
          style: TextStyle(
            color: Colors.white,
            fontSize: 20,
            fontWeight: FontWeight.w700,
          ),
        ),
      ),
      const SizedBox(width: 12),
      Expanded(
        child: Text(
          'Ambit Optima Integrator',
          style: Theme.of(context).textTheme.titleLarge
              ?.copyWith(fontWeight: FontWeight.w600),
        ),
      ),
      if (refreshing)
        const Padding(
          padding: EdgeInsets.all(12),
          child: SizedBox.square(
            dimension: 18,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
        )
      else
        IconButton(
          tooltip: 'Odśwież',
          icon: const Icon(Icons.refresh),
          onPressed: onRefresh,
        ),
    ],
  );
}

class _Summary extends StatelessWidget {
  const _Summary({required this.health});

  final AgentHealth health;

  @override
  Widget build(BuildContext context) {
    final dark = Theme.of(context).brightness == Brightness.dark;
    final (title, text, icon, tone) = switch (health) {
      AgentHealth.checking => (
        'Sprawdzanie agenta…',
        'Łączenie z usługą.',
        Icons.hourglass_empty,
        _Tone.neutral,
      ),
      AgentHealth.healthy => (
        'Agent działa',
        'Usługa pracuje i łączy się z SQL Server.',
        Icons.check_circle,
        _Tone.good,
      ),
      AgentHealth.overprivileged => (
        'Agent ma za szerokie uprawnienia',
        'Konto usługi ma w SQL Server rolę sysadmin lub db_owner. '
            'Powinno mieć tylko uprawnienia nadane przez instalator.',
        Icons.warning_amber_rounded,
        _Tone.warning,
      ),
      AgentHealth.sqlUnavailable => (
        'Brak połączenia z SQL Server',
        'Usługa działa, ale nie może połączyć się z bazą. '
            'Sprawdź, czy SQL Server jest uruchomiony.',
        Icons.error,
        _Tone.bad,
      ),
      AgentHealth.stopped => (
        'Agent nie odpowiada',
        'Usługa AmbitOptimaIntegrator jest zatrzymana albo nie odpowiada. '
            'Uruchom ją w Usługach Windows lub poproś administratora.',
        Icons.error,
        _Tone.bad,
      ),
    };
    final colors = tone.colors(dark, Theme.of(context).colorScheme);

    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: colors.background,
        border: Border.all(color: colors.border),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, color: colors.foreground, size: 30),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    color: colors.foreground,
                    fontWeight: FontWeight.w600,
                  ),
                ),
                const SizedBox(height: 4),
                Text(text, style: Theme.of(context).textTheme.bodyMedium),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

enum _Tone {
  neutral,
  good,
  warning,
  bad;

  ({Color background, Color border, Color foreground}) colors(
    bool dark,
    ColorScheme scheme,
  ) => switch (this) {
    _Tone.neutral => (
      background: scheme.surface,
      border: scheme.outlineVariant,
      foreground: scheme.onSurface,
    ),
    _Tone.good =>
      dark
          ? (
              background: const Color(0xFF10291C),
              border: const Color(0xFF1F5135),
              foreground: const Color(0xFF6EE7A0),
            )
          : (
              background: const Color(0xFFEDF9F1),
              border: const Color(0xFFB7E4C7),
              foreground: const Color(0xFF166534),
            ),
    _Tone.warning =>
      dark
          ? (
              background: const Color(0xFF2E2110),
              border: const Color(0xFF5F4318),
              foreground: const Color(0xFFFBBF5C),
            )
          : (
              background: const Color(0xFFFFF7E8),
              border: const Color(0xFFF3D9A4),
              foreground: const Color(0xFF92400E),
            ),
    _Tone.bad =>
      dark
          ? (
              background: const Color(0xFF321716),
              border: const Color(0xFF692B28),
              foreground: const Color(0xFFFCA5A0),
            )
          : (
              background: const Color(0xFFFEF1F0),
              border: const Color(0xFFF6C9C5),
              foreground: const Color(0xFFB42318),
            ),
  };
}

class _Details extends StatelessWidget {
  const _Details({required this.title, required this.icon, required this.rows});

  final String title;
  final IconData icon;
  final List<(String, String)> rows;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 12),
      decoration: BoxDecoration(
        color: theme.colorScheme.surface,
        border: Border.all(color: theme.colorScheme.outlineVariant),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(icon, size: 18, color: theme.colorScheme.onSurfaceVariant),
              const SizedBox(width: 8),
              Text(
                title,
                style: theme.textTheme.titleSmall?.copyWith(
                  fontWeight: FontWeight.w600,
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          for (final (label, value) in rows)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 5),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SizedBox(
                    width: 130,
                    child: Text(
                      label,
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    ),
                  ),
                  // Selectable: a login or server name is what people paste into a support request.
                  Expanded(
                    child: SelectableText(
                      value,
                      style: theme.textTheme.bodyMedium,
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

class _Footer extends StatelessWidget {
  const _Footer({required this.checkedAt, required this.serviceAnswers});

  final DateTime? checkedAt;
  final bool serviceAnswers;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final style = theme.textTheme.bodySmall?.copyWith(
      color: theme.colorScheme.onSurfaceVariant,
    );
    final at = checkedAt;
    return Text(
      '${at == null ? '' : 'Sprawdzono o ${_time(at)}. '}'
      '${serviceAnswers ? 'Usługa działa także po zamknięciu tego okna.' : ''}',
      style: style,
    );
  }
}
