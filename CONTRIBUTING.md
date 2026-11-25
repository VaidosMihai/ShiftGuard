# Contributing to WinPerfOptimizer

1. Fork the repository and create a feature branch.
2. Ensure all code builds with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (`dotnet build -c Release`).
3. Add or update xUnit tests in `tests/WinPerfOptimizer.Core.Tests` or `tests/WinPerfOptimizer.App.Tests`.
4. Never weaken `Guardrails` or introduce unvalidated shell execution.
