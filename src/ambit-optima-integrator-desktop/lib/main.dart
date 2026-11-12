import 'package:flutter/material.dart';

import 'status_page.dart';

void main() => runApp(const AgentApp());

class AgentApp extends StatelessWidget {
  const AgentApp({super.key});

  static ThemeData _theme(Brightness brightness) {
    final dark = brightness == Brightness.dark;
    return ThemeData(
      useMaterial3: true,
      colorScheme: ColorScheme.fromSeed(
        seedColor: const Color(0xFF0F766E),
        brightness: brightness,
        surface: dark ? const Color(0xFF1C2024) : Colors.white,
      ),
      scaffoldBackgroundColor: dark
          ? const Color(0xFF131619)
          : const Color(0xFFF5F6F8),
      visualDensity: VisualDensity.compact,
    );
  }

  @override
  Widget build(BuildContext context) => MaterialApp(
    title: 'Ambit Optima Integrator',
    debugShowCheckedModeBanner: false,
    theme: _theme(Brightness.light),
    darkTheme: _theme(Brightness.dark),
    home: const StatusPage(),
  );
}
