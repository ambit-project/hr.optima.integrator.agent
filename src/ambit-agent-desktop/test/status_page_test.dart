import 'package:ambit_agent_desktop/ipc/agent_client.dart';
import 'package:ambit_agent_desktop/ipc/ipc_message.dart';
import 'package:ambit_agent_desktop/status_page.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

const _running = IpcResponse(
  ok: true,
  data: {
    'worker': 'Running',
    'version': '0.2.0.0',
    'startedAt': '2026-11-06T20:18:00+00:00',
  },
);

IpcResponse _sql({bool overprivileged = false}) => IpcResponse(
  ok: true,
  data: {
    'serverName': r'HOST\OPTIMA',
    'loginName': r'NT SERVICE\AmbitOptimaAgent',
    'isOverprivileged': overprivileged,
  },
);

Future<void> _show(
  WidgetTester tester,
  Future<IpcResponse> Function(String command) transport,
) async {
  await tester.pumpWidget(
    MaterialApp(
      home: StatusPage(client: AgentClient(transport: transport)),
    ),
  );
  await tester.pumpAndSettle();
}

/// Removes the page, whose timer would otherwise fail the test as still pending.
Future<void> _close(WidgetTester tester) => tester.pumpWidget(const SizedBox());

void main() {
  testWidgets('a working agent shows its service and SQL details', (
    tester,
  ) async {
    await _show(
      tester,
      (command) async => command == 'status' ? _running : _sql(),
    );

    expect(find.text('Agent działa'), findsOneWidget);
    expect(find.text('0.2.0'), findsOneWidget);
    expect(find.text(r'HOST\OPTIMA'), findsOneWidget);
    expect(find.text(r'NT SERVICE\AmbitOptimaAgent'), findsOneWidget);
    await _close(tester);
  });

  testWidgets('a stopped service says what to do', (tester) async {
    await _show(
      tester,
      (_) async => throw const WorkerUnavailable('Win32 error 2'),
    );

    expect(find.text('Agent nie odpowiada'), findsOneWidget);
    expect(find.text('Nie odpowiada'), findsOneWidget);
    await _close(tester);
  });

  testWidgets('a service without SQL Server is told apart from a stopped one', (
    tester,
  ) async {
    await _show(
      tester,
      (command) async => command == 'status'
          ? _running
          : const IpcResponse(ok: false, error: 'sql_unavailable:SqlException'),
    );

    expect(find.text('Brak połączenia z SQL Server'), findsOneWidget);
    expect(find.text('Działa'), findsOneWidget);
    await _close(tester);
  });

  testWidgets('an overprivileged account is said out loud', (tester) async {
    await _show(
      tester,
      (command) async =>
          command == 'status' ? _running : _sql(overprivileged: true),
    );

    expect(find.text('Agent ma za szerokie uprawnienia'), findsOneWidget);
    await _close(tester);
  });

  testWidgets('the status refreshes on its own', (tester) async {
    var calls = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: StatusPage(
          refreshEvery: const Duration(seconds: 1),
          client: AgentClient(
            transport: (command) async {
              if (command == 'status') calls++;
              return command == 'status' ? _running : _sql();
            },
          ),
        ),
      ),
    );
    await tester.pump();
    expect(calls, 1);

    await tester.pump(const Duration(seconds: 1));
    await tester.pump();

    expect(calls, 2);
    await _close(tester);
  });
}
