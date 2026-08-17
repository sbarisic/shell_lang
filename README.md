# ShellLang

ShellLang is a typed command and dataflow language for in-process .NET hosts.

Commands exchange typed values instead of byte streams. A host can expose game objects, tools, services, or other local values through explicit descriptors.

```shelllang
target = find_entities(classname: "info_spawn")
    -> where(.spawn_order > 1)
    -> first
    -> require

target.position -> print
```

`this` names the effective value at the current operation. Host types can also expose one explicit constructor:

```shelllang
local_player -> give_credits(amount: this.name_length()) -> require
transform = Transform(Vector3(1, 2, 3), Quaternion(), Vector3(1, 1, 1))
transform.position.x -> print
gravity = Physics.gravity
weapons = Weapon.values
precise = Float64(1)
small = Float32(1) -> require
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

The `shell_lang_test` project is an interactive in-game-style console and bootstrap executable. The `shell_lang_tests` project contains the discoverable conformance suite, and `shell_lang_test_support` provides their shared deterministic mock-game host.

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
- Core conversions are explicit; checked conversions return `Result<T,ConversionError>`.
- Types can expose read-only scoped values, and every enum exposes `.values` in declaration order.
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

Version 0.1 does not include functions, loops, blocks, lambdas, async commands, executable streams, reflection-based construction, property mutation, or OS process execution.

These limits keep the first implementation focused on typed commands, connection adaptation, arrays, Results, and explicit host descriptors.

## Build

The project requires the .NET 10 SDK.

```powershell
dotnet build .\shell_lang.slnx
```

Start the interactive console, run the conformance suite, or execute the full bootstrap example:

```powershell
dotnet run --project .\shell_lang_test
dotnet test .\shell_lang.slnx
dotnet run --project .\shell_lang_test -- --example
```

The console keeps one session alive, so bindings carry over between entries. Enter a ShellLang expression and press Enter to evaluate it. REPL results and the `print` command use the same descriptor-aware formatter: strings are quoted, arrays and Results are recursive, registered members are visible, and opaque host values show only their ShellLang type name. Successful commands with no result remain silent. Type `help` to list the registered commands and intrinsics, `help <name>` for detailed symbol help, or `exit`/`quit` to close.

## License

ShellLang uses the [MIT License](LICENSE).
