# ShellLang

ShellLang is a typed command and dataflow language for in-process .NET hosts.

Commands exchange typed values instead of byte streams. A host can expose game objects, tools, services, or other local values through explicit descriptors.

```text
target = players
    -> where(.health < 50)
    -> first

target
    -> require
    -> damage(amount: 10, type: Fire)
```

The language uses a small set of connection forms:

```text
value -> command(argument: value)
command(port <- value, argument: value)
value.member
```

`->` connects a value to a command's default input. `<-` connects an explicit input port. `:` supplies an argument.

## Status

ShellLang 0.1 is implemented as a dependency-free .NET 10 library. It includes the handwritten parser, static binder, descriptor catalog, synchronous runtime, collection intrinsics, diagnostics, help, and completion APIs.

The `shell_lang_test` console project is both the conformance runner and a deterministic mock-game host for the complete map bootstrap example.

## Specifications

- [Language specification](LANGUAGE.md) defines syntax, types, pipelines, lifting, Results, faults, and collection intrinsics.
- [C# hosting contract](HOSTING.md) defines descriptors, sessions, compilation, execution, diagnostics, and the host security boundary.

The [map bootstrap example](EXAMPLE.md) shows a complete 280-line ShellLang script for an in-game map.

The specification documents are normative. This README is only an introduction.

## Core behavior

- Values have static nominal types.
- Commands declare typed input ports, arguments, output ports, and errors.
- Whole-value compatibility wins before automatic adaptation.
- A scalar operation maps over `Array<T>` when no array-consuming connection exists.
- `Result<T,E>` propagates errors without invoking downstream operations.
- Only registered CLR values and operations are visible.
- The runtime executes synchronously and preserves array order.

For example, a command that accepts one `Player` maps over `Array<Player>`:

```text
players -> damage(amount: 10)
```

A command that accepts the complete array runs once:

```text
players.health -> sum -> print
```

## Version 0.1 boundaries

Version 0.1 does not include functions, loops, blocks, lambdas, async commands, executable streams, reflection, property mutation, or OS process execution.

These limits keep the first implementation focused on typed commands, connection adaptation, arrays, Results, and explicit host descriptors.

## Build

The project requires the .NET 10 SDK.

```powershell
dotnet build .\shell_lang.slnx
```

Run the conformance cases and example together, or select one mode:

```powershell
dotnet run --project .\shell_lang_test
dotnet run --project .\shell_lang_test -- --tests
dotnet run --project .\shell_lang_test -- --example
```

## License

ShellLang uses the [MIT License](LICENSE).
