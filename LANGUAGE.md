# ShellLang 0.1 Language Specification

## 1. Status

This document defines ShellLang 0.1.

ShellLang is a synchronous, statically typed command and dataflow language. A host exposes all accessible values and operations through descriptors.

The terms **MUST**, **MUST NOT**, **SHOULD**, and **MAY** define conformance requirements.

An implementation conforms to ShellLang 0.1 when it follows this document and the hosting rules in `HOSTING.md`.

## 2. Core model

Every ShellLang value has a static type. A command has typed input ports, arguments, and output ports.

The `->` operator connects a value to a command's default input port.

```text
player -> damage(amount: 10)
```

The `<-` operator connects an expression to an explicit input port.

```text
attack(
    attacker <- player,
    target <- enemy,
    weapon <- player.inventory.primary,
    power: 0.8
)
```

The `:` token supplies an argument. Arguments configure an operation and are not dataflow inputs.

The `.` operator reads registered data. It never exposes unregistered CLR data.

```text
player.health
player.position
player.distance(to: local_player)
```

ShellLang can adapt a scalar operation over an immutable array. An exact whole-value connection always wins before adaptation.

```text
players -> damage(amount: 10)  # Map damage(Player) over the array.
players.health -> sum          # Call sum(Array<Int32>) once.
```

## 3. Source text

### 3.1 Encoding and case

Source text MUST use Unicode. An implementation SHOULD accept UTF-8 without a byte-order mark for files.

Identifiers are case-sensitive. `player`, `Player`, and `PLAYER` are different identifiers.

### 3.2 Whitespace and comments

Spaces, tabs, and continuation newlines separate tokens. Indentation has no semantic meaning.

The `#` token starts a line comment outside a string. The comment continues to the next newline.

```text
# Damage each injured player.
players -> where(.health < 50) -> damage(amount: 10)
```

### 3.3 Identifiers

An identifier starts with a Unicode letter or `_`. Later characters can also contain Unicode decimal digits.

The names `true`, `false`, `this`, and `null` are reserved. `this` is the contextual value described in Section 10. `null` is not a valid ShellLang value in version 0.1.

Hosts SHOULD use `snake_case` for commands, ports, arguments, globals, and members. Hosts SHOULD use `PascalCase` for types and enum members.

### 3.4 Statement termination

A semicolon is a hard statement terminator. It terminates the current statement even when the following token could continue it.

A newline terminates a statement unless one of these conditions is true:

1. The newline occurs inside `()` or `[]`.
2. The preceding token requires a following operand.
3. The next non-comment token is `->` or `.`.

Tokens that require a following operand include operators, commas, `:`, `<-`, `->`, `=`, and opening delimiters.

Comments do not affect the look-ahead rule.

These pipelines are equivalent:

```text
players -> where(.health < 50) -> kill
```

```text
players
    -> where(.health < 50)
    -> kill
```

Member access can also continue on the next line:

```text
players
    -> where(.team == Red)
    .health
    -> average
```

The following source contains two statements because the semicolon forces termination:

```text
players;
-> kill # Static syntax error. The second statement cannot start with ->.
```

### 3.5 Literals

ShellLang 0.1 supports these literals:

- `true` and `false` for `Bool`
- Integer numeric tokens
- Fractional numeric tokens
- Double-quoted strings
- Immutable array literals

Numeric tokens use base 10. They do not support type suffixes or digit separators.

An integer token contains decimal digits only. A fractional token contains a decimal point, an exponent, or both.

An exponent uses `e` or `E`, an optional sign, and one or more decimal digits. A leading `-` is a unary operator.

Strings support `\\`, `\"`, `\n`, `\r`, `\t`, and `\uXXXX` escapes. Other escape sequences are syntax errors.

```shelllang
message = "player\nready"
numbers = [1, 2, 3]
```

An array literal uses an expected `Array<T>` type when one exists. Each element MUST be assignable to `T`.

Without an expected type, all array elements MUST infer the same static type. Contextual numeric literals can adopt that type.

An empty array literal requires an expected `Array<T>` type. A standalone assignment such as `items = []` is a static error.

## 4. Grammar

This grammar describes the accepted source forms. Semantic rules can reject a grammatically valid form.

```ebnf
script              = { terminator },
                      [ statement, { terminator, statement } ],
                      { terminator } ;

terminator          = newline | ";" ;

statement           = assignment | expression ;
assignment          = identifier, "=", expression ;

expression          = pipeline_expression ;
pipeline_expression = logical_or_expression,
                      { "->", pipeline_stage } ;

pipeline_stage      = ( identifier | invocation ),
                      { member_suffix } ;

logical_or_expression
                    = logical_and_expression, { "||", logical_and_expression } ;
logical_and_expression
                    = equality_expression, { "&&", equality_expression } ;
equality_expression = order_expression, { ( "==" | "!=" ), order_expression } ;
order_expression    = additive_expression,
                      { ( "<" | "<=" | ">" | ">=" ), additive_expression } ;
additive_expression = multiplicative_expression,
                      { ( "+" | "-" ), multiplicative_expression } ;
multiplicative_expression
                    = unary_expression,
                      { ( "*" | "/" | "%" ), unary_expression } ;
unary_expression    = [ "!" | "-" ], postfix_expression ;

postfix_expression  = primary_expression,
                      { member_suffix } ;

member_suffix       = ".", identifier, [ member_arguments ] ;

primary_expression  = literal
                    | identifier
                    | "this"
                    | invocation
                    | array_literal
                    | contextual_member
                    | "(", expression, ")" ;

literal             = boolean_literal
                    | integer_literal
                    | fractional_literal
                    | string_literal ;

contextual_member   = ".", identifier, [ member_arguments ] ;

invocation          = identifier, "(", [ invocation_entries ], ")" ;
invocation_entries  = invocation_entry, { ",", invocation_entry }, [ "," ] ;
invocation_entry    = explicit_input
                    | named_argument
                    | expression ;
explicit_input      = identifier, "<-", expression ;
named_argument      = identifier, ":", expression ;

member_arguments    = "(", [ argument_entries ], ")" ;
argument_entries    = argument_entry, { ",", argument_entry }, [ "," ] ;
argument_entry      = named_argument | expression ;

array_literal       = "[", [ expression, { ",", expression }, [ "," ] ], "]" ;
```

A positional argument MUST occur before all named arguments and explicit inputs in the same invocation.

An explicit input is valid only in a command invocation. A member query cannot contain `<-`.

A pipeline stage can omit `()` only when the command or intrinsic has no supplied arguments.

A command used without a pipeline source MUST use invocation syntax. A zero-input command therefore uses `command()`.

The lexer supplies the `boolean_literal`, `integer_literal`, `fractional_literal`, and `string_literal` terminals described in Section 3. The binder permits `this` and `contextual_member` only where an effective contextual value exists. A leading `.member` is shorthand for `this.member`.

## 5. Names and assignments

### 5.1 Name resolution

The compiler resolves a bare value identifier in this order:

1. A current session binding
2. A registered global
3. A contextual enum member

A command name resolves only in an invocation or pipeline-stage position. Command values are not first-class values.

A type name resolves in a type context, as the left side of a type-scoped value, or as a type invocation. A type-qualified name resolves before a same-spelled binding or global. A type invocation resolves before command lookup and denotes either the host type's constructor or an engine-owned core conversion. An invocable type name cannot collide with a command or intrinsic.

```text
DamageType.Fire
```

Command names MUST be unique. ShellLang 0.1 does not support command overloads.

Compiler intrinsics also have unique names. A host cannot register a command with an intrinsic name.

### 5.2 Assignment

An assignment has one bare identifier on its left side.

```text
target = players -> first
```

An assignment creates or replaces a session binding. The static type of the right side becomes the binding type for later statements.

Rebinding can change a binding's type:

```text
x = "hello" # x : String
x = 10      # x : Int32
```

Property assignment is not valid:

```text
player.health = 100 # Static error.
```

An assignment evaluates its right side before it changes the session. A runtime fault leaves the previous binding unchanged.

A typed `Err` is a value. An assignment can store it as a `Result<T,E>` value.

An assignment cannot bind `Void`.

### 5.3 Compilation requirements

The compiler processes statements in source order. Each successful static assignment updates the compile-time symbol table.

A compilation records each pre-existing session binding that its source reads. It records the required name and static type.

Execution MUST validate these requirements before the first statement. A missing binding or a changed type causes a stale-compilation host fault.

A changed value with the same static type does not invalidate a compilation.

## 6. Type system

### 6.1 Nominal types

ShellLang uses nominal types. A type is compatible with its declared base types and interfaces only.

The host MUST register all host type relationships. ShellLang does not inspect CLR inheritance automatically.

All value types are non-null. ShellLang 0.1 has no nullable type and no null literal.

### 6.2 Core types

ShellLang 0.1 defines these core types:

| Type | Meaning |
| --- | --- |
| `Bool` | Boolean value |
| `Int32` | Signed 32-bit integer |
| `Int64` | Signed 64-bit integer |
| `UInt32` | Unsigned 32-bit integer |
| `UInt64` | Unsigned 64-bit integer |
| `Float32` | IEEE 754 binary32 value |
| `Float64` | IEEE 754 binary64 value |
| `String` | Unicode string |
| `Array<T>` | Immutable, finite, materialized sequence |
| `Result<T,E>` | Success value or typed error |
| `Any` | Top type for all values |
| `Error` | Root type for declared errors |
| `Void` | Absence of an output value |

`EmptyCollectionError`, `CollectionCardinalityError`, and `ConversionError` are core errors derived directly from `Error`. `CollectionCardinalityError` records the actual element count. `ConversionError` records source type, target type, and a safe failure reason.

`Void` is not a value type. It is not assignable to `Any`, cannot enter an array, and cannot feed another operation.

A fallible zero-output command has the type `Result<Void,E>`. Its `Ok` case has no payload.

`require` can consume `Result<Void,E>`. A successful `require` produces terminal `Void` without creating a `ShellValue`.

### 6.3 Any

Every value type is assignable to `Any`.

`Any` is not implicitly assignable to a more specific type. ShellLang 0.1 has no cast or runtime type-test expression.

An `Any` value exposes only members that the host registers on `Any`.

Whole-value compatibility has the highest adaptation priority. An operation that accepts `Any` receives the complete value.

For example, `print : Any -> Void` receives an entire `Array<Player>` or `Result<Player,E>`. It does not map or unwrap that value.

### 6.4 Array variance

`Array<T>` is immutable and covariant.

If `Player` is assignable to `Entity`, then `Array<Player>` is assignable to `Array<Entity>`.

Covariance lets an array-consuming command accept a more specific element type:

```text
players -> kill_team # kill_team accepts Array<Entity>.
```

### 6.5 Result variance and error inheritance

`Result<T,E>` is covariant in both type parameters.

If `Player` is assignable to `Entity`, and `ReadError` derives from `Error`, then:

```text
Result<Player, ReadError> <: Result<Entity, Error>
```

Every error type MUST have one direct error base. The chain MUST end at `Error`.

ShellLang combines two error types with their nearest common error base. A single base chain makes this result deterministic.

```text
Error
├── IOError
│   ├── ReadError
│   └── WriteError
└── GameError
```

The common error type for `ReadError` and `WriteError` is `IOError`. The common type for `ReadError` and `GameError` is `Error`.

### 6.6 Output record types

A command with several outputs creates one nominal output record type. Its fields have the output port names and types.

The record is immutable. The record descriptor can declare one field as its default output.

Output record types do not use structural compatibility. Two commands with identical output fields still produce different nominal types.

### 6.7 Stream reservation

The generic name `Stream<T>` is reserved for a later language version.

A ShellLang 0.1 host MUST reject a descriptor that uses `Stream<T>`. The compiler MUST reject an executable expression that uses it.

## 7. Contextual literals and primitive operators

### 7.1 Numeric literals

A numeric token has no final numeric type until the compiler applies context.

An integer token can adopt any core integer type when its value is in range. It can also adopt `Float32` or `Float64`.

A fractional token can adopt `Float32` or `Float64`.

Floating-point contextual conversion uses round-to-nearest with ties-to-even. The compiler rejects a literal that converts to infinity.

Without an expected type, an integer token defaults to `Int32`. A value outside the `Int32` range is a static error without context.

Without an expected type, a fractional token defaults to `Float64`.

Contextual typing applies to literals only. A typed runtime number never widens or narrows implicitly.

```text
x = 10                         # Int32
damage(amount: 10)             # Uses the declared amount type.
teleport(speed: 10)            # Can use Float32 when speed expects Float32.
```

The compiler MUST reject a contextual literal that the target type cannot represent.

### 7.2 Enum members

A bare identifier can adopt an expected enum type when that enum contains the member.

```text
player -> damage(type: Fire)
```

The explicit `EnumType.Member` form does not need an expected type.

If more than one active context could supply an enum type, the compiler MUST require the explicit form.

Every enum also has a reserved `values` scoped value. `Weapon.values` produces a new immutable `Array<Weapon>` in registered declaration order. A host cannot declare an enum member named `values`.

Nominal host types can declare fixed or provider-backed read-only values with the same qualification syntax:

```shelllang
origin = Vector3.zero
gravity = Physics.gravity
weapons = Weapon.values
```

A provider runs once for each reference. The engine validates its ShellLang type and CLR payload and contains provider exceptions as host faults. Type-scoped values are normal expressions, so they can be assigned, passed to commands, stored in arrays, or piped. ShellLang does not discover CLR static fields or properties.

### 7.3 Operator precedence

Operators use this precedence from highest to lowest:

1. Member access and member query calls
2. Unary `!` and `-`
3. `*`, `/`, and `%`
4. `+` and `-`
5. `<`, `<=`, `>`, and `>=`
6. `==` and `!=`
7. `&&`
8. `||`
9. `->`

Binary operators associate from left to right. The pipeline operator also associates from left to right.

Evaluation proceeds from left to right. `&&` and `||` short-circuit after Result propagation.

### 7.4 Operator types

Arithmetic operands MUST have the same static numeric type after contextual literal typing.

`+`, `-`, `*`, and `/` return that numeric type. Integer division truncates toward zero.

`%` accepts integer operands only. Unary `-` accepts signed integers and floating-point values only.

Integer arithmetic uses checked operations. Integer overflow is a runtime fault.

Division by numeric zero is a runtime fault for integer and floating-point operations.

Ordering operators accept matching numeric types, `String`, enums with a registered order, or host types with a registered order.

String ordering uses ordinal comparison.

Equality accepts matching primitive or enum types. A host type MUST register equality before scripts can compare its values.

## 8. Operations and connection adaptation

### 8.1 Operation model

These language features are typed operations:

- Commands
- Member reads
- Registered member queries
- Primitive operators
- Compiler intrinsics

Each operation has one primary input. A command's default input is its primary input.

A member read or query uses its receiver as the primary input. A binary operator uses its left operand as the primary input.

Other command inputs, arguments, query arguments, and right operands are secondary inputs.

### 8.2 Adaptation algorithm

The compiler connects a value to an operation parameter with this ordered algorithm:

1. If the complete value type is assignable to the parameter type, connect it directly.
2. If the value is `Result<T,E>`, apply the connection recursively to a successful `T`.
3. If the value is an output record with a default output, apply the connection recursively to that field.
4. If array lifting is allowed and the value is `Array<T>`, apply the operation recursively to each `T`.
5. Otherwise, report a static connection error.

The compiler repeats the whole-value test at every recursive layer.

Array lifting is allowed only for the primary input. Secondary inputs can use Result propagation and default-output projection, but cannot use array lifting.

This algorithm produces these results:

```text
Trace.Output -> inspect_trace # Pass the complete record when accepted.
Trace.Output -> kill          # Project the default Entity output.
Array<Player> -> kill_team    # Pass the complete covariant array.
Array<Player> -> kill         # Invoke kill once for each Player.
```

The algorithm applies recursively:

```text
Result<Array<Trace.Output>, E> -> kill
```

The compiler propagates the Result, maps over the array, projects each default output, and invokes `kill`.

The algorithm does not change array elements to satisfy an array-consuming input.

```text
Array<Trace.Output> -> inspect_entities # Expects Array<Entity>. Static error.
Array<Trace.Output>.entity -> inspect_entities # Valid explicit projection.
```

### 8.3 Member adaptation

The compiler first searches for a member on the complete receiver type. It applies Result propagation, default-output projection, and array lifting only when that search fails.

```text
players.health # Array<Player> becomes Array<Int32>.
```

Member queries use the same rule:

```text
(players -> first).distance(to: local_player)
```

If `first` succeeds, the query runs on its `Player`. If `first` fails, the query does not run.

### 8.4 Default output precedence

A multi-output record remains a complete value until an operation needs its default output.

Assignment stores the complete record:

```text
trace_result = trace(origin: start, direction: forward)
hit_point = trace_result.hit
```

Whole-record compatibility always wins:

```text
trace_result -> inspect_trace # inspect_trace accepts Trace.Output.
trace_result -> kill          # kill accepts Entity, so use the default entity field.
```

A record without a default output cannot use implicit projection.

## 9. Result propagation and faults

### 9.1 Declared errors

A fallible operation returns `Result<T,E>`. An `Ok` contains `T` when `T` is a value type.

An `Ok` for `Result<Void,E>` contains no payload. An `Err` contains a value of `E` and runtime context frames.

An `Err` is an ordinary typed value. It does not abort a script by itself.

### 9.2 General propagation

Result propagation applies to every operation, not only commands.

If an operation accepts the complete Result type, direct connection wins. Otherwise, the operation applies to the successful value.

```text
Result<Player, E>.health
    => Result<Int32, E>
```

If the operation is also fallible, the compiler flattens the result:

```text
Result<A, E1> -> operation(A -> Result<B, E2>)
    => Result<B, common_error(E1, E2)>
```

An operation with several Result-valued inputs evaluates them from left to right. The first runtime `Err` becomes the propagated error.

After an input produces a propagated `Err`, the runtime does not evaluate later secondary input expressions.

The compiler uses the nearest common error base for the static error type.

### 9.3 Standard Result intrinsics

ShellLang defines these generic intrinsics:

| Intrinsic | Input | Output | Error behavior |
| --- | --- | --- | --- |
| `require` | `Result<T,E>` | `T` | Converts `Err` to a runtime fault |
| `value_or(default: T)` | `Result<T,E>` | `T` | Returns the default for `Err` |
| `error` | `Result<T,E>` | `E` | Converts `Ok` to a runtime fault |
| `is_ok` | `Result<T,E>` | `Bool` | Never faults |

These intrinsics accept the complete Result. Direct connection therefore wins before Result propagation.

`value_or` requires a value success type. It is not available for `Result<Void,E>` because `Void` cannot be an argument value.

```text
players -> first -> require -> kill
```

### 9.4 Runtime faults and host faults

A runtime fault is a defined language failure that is not a typed error. Examples include `require` on `Err`, integer overflow, and division by zero.

A registered command MAY return a runtime fault that its descriptor declares. This outcome is not an `Err` and cannot propagate as a value.

A command runtime fault MUST contain a stable host-defined runtime fault code and a safe message. Core runtime fault codes use the `SL4xxx` range.

Host runtime fault codes MUST use a different namespace, such as `GAME1001`.

A host fault reports a broken host boundary or stale execution state. Examples include:

- An unexpected CLR exception
- A null CLR value for a non-null type
- A value that fails its registered CLR adapter
- A changed command or type catalog
- An invalid initial session requirement

A runtime fault or host fault aborts the current compilation. The runtime MUST NOT execute later statements.

A runtime fault during array lifting also stops the lift. The runtime MUST NOT invoke the operation for later elements.

Completed assignments and command side effects remain committed. ShellLang 0.1 does not provide rollback.

## 10. Commands and invocation

### 10.1 Command shape

A command descriptor defines:

- One unique name
- A description
- Zero or one default input port
- Zero or more additional input ports
- Zero or more arguments
- Zero or more output ports
- Zero or one declared error type
- Zero or more declared runtime fault codes
- One synchronous invoker

Input ports carry data. Arguments configure an invocation.

All required ports and arguments MUST receive one value. An invocation MUST NOT supply the same port or argument more than once.

### 10.2 Default and explicit inputs

The pipeline source connects to the default input:

```text
player -> damage(amount: 10)
```

The same connection can use an explicit port:

```text
damage(target <- player, amount: 10)
```

An invocation cannot supply a default input through both forms.

A command without a default input cannot appear after `->`.

### 10.3 Invocation evaluation

The runtime evaluates a pipeline source once.

If an outer propagated Result is already `Err`, the runtime skips the command. It also skips all explicit inputs and arguments for that command.

Otherwise, the runtime divides explicit inputs and arguments into ordinary and contextual groups. An expression is contextual when any part of it references the current operation's `this`. The runtime evaluates all ordinary expressions once, in their relative source order, before direct invocation or array lifting. It evaluates contextual expressions in their relative source order once for each effective invocation. It does not partially hoist a context-free subexpression out of a contextual expression.

```text
players -> damage(
    source <- local_player,
    amount: random_damage()
)
```

The example has these steps:

1. Evaluate `players` once.
2. Evaluate `local_player` once.
3. Evaluate `random_damage()` once.
4. Invoke `damage` for each player in array order.

In the following example, `random_damage()` runs once per player because the complete argument references `this`:

```text
players -> damage(amount: random_damage() + this.health)
```

An empty source array evaluates ordinary explicit inputs and arguments once, evaluates no contextual expressions, and performs zero command invocations.

If a secondary value produces an unhandled `Err`, the runtime skips the command and propagates that error.

An array secondary value MUST match an array parameter directly. The runtime never maps or zips a secondary value.

### 10.4 Positional and named arguments

Positional arguments bind in descriptor order. They MUST appear before named arguments and explicit inputs.

Named arguments bind by name and can appear in any order after positional arguments.

```text
resize(image <- source, width: 512)
```

Named arguments are the preferred style for scripts and documentation.

### 10.5 Outputs

A command with no output returns `Void`. A fallible command with no output returns `Result<Void,E>` with payloadless success.

A command with one output returns that output type directly. Its output name remains available in metadata.

A command with several outputs returns its generated output record. The record can declare one default output.

The runtime validates every output value against its descriptor before it returns the value to the script.

### 10.6 Contextual `this`

A pipeline primary and a member or query receiver introduce a lexical contextual scope for their secondary expressions. `this` has the static type of the effective value that reaches the operation after Result propagation and default-output projection. A directly assignable derived value retains its derived static type.

For scalar lifting, `this` is the current adapted element. For an operation that directly consumes the complete array, `this` is that `Array<T>` value. A nested contextual operation introduces a new scope; its `this` shadows the outer value and the outer scope is restored afterward. A leading `.member` is exactly equivalent to `this.member`.

`this` is invalid at top level, as an assignment target, and in a standalone invocation that has no enclosing contextual scope. A standalone command's explicit default input does not introduce a scope, but an expression used with `<-` can consume an already enclosing scope.

### 10.7 Host constructor expressions

A nominal host type can declare one constructor. `TypeName(...)` invokes it as an expression before command lookup. Constructors accept positional and named arguments with the same ordering, required, and constant-default rules as commands.

```shelllang
point = Vector3(1, 2, 3)
transform = Transform(point, Quaternion(), Vector3(1, 1, 1))
transform.position.y -> print
```

Constructors cannot be pipeline stages and do not accept `<-` entries. Arguments evaluate once in source order in the enclosing contextual scope, so nested constructors can reference `this`. The first argument `Err` propagates without invoking the constructor. A fallible constructor combines its declared error with argument errors by the nearest-common-error rule.

`SL2501` reports a call to a non-constructible type, `SL2502` reports constructor use as a pipeline stage, and `SL2503` reports a constructor-specific invalid entry such as `<-`.

Constructors are synchronous and contractually pure. Their values, errors, and CLR payloads are validated at the host boundary. Delegate exceptions and invalid outcomes are contained as host faults. ShellLang never discovers constructors through reflection.

### 10.8 Explicit core conversions

`TargetType(value)` is an engine-owned conversion when `TargetType` is a supported core target. The operand is bound without the target's contextual literal type, evaluates once, and can be any supported numeric source. A conversion is an expression, cannot be a pipeline stage, and does not accept `<-` entries.

Identity numeric conversions and these widening conversions are guaranteed and return the target type directly:

- `Int32` to `Int64` or `Float64`
- `UInt32` to `Int64`, `UInt64`, or `Float64`
- `Float32` to `Float64`

Every other numeric cross-conversion is checked and returns `Result<T,ConversionError>`. Floating-to-integer conversion requires a finite, integral, in-range value. Integer narrowing and integer-to-floating conversion require an exact round trip. Floating narrowing requires a finite, exactly representable result. `NaN` and infinities survive only numeric identity and `Float32` to `Float64`; other numeric cross-conversions return `ConversionError`.

`String(value)` is guaranteed for `Bool`, numeric core values, strings, and enums. It uses lowercase Boolean text, invariant canonical numeric text, and the registered ShellLang enum member name. It does not parse strings, convert numbers to `Bool`, invoke CLR conversion operators, or apply implicitly.

```shelllang
precise = Float64(10)
checked = Float32(10) -> require
weapon_name = String(Weapon.Shotgun)
```

If the operand is an `Err`, the conversion does not run and the error propagates. A checked conversion combines the operand error with `ConversionError` through the nearest-common-error rule.

## 11. Array lifting

### 11.1 Value-producing scalar lifting

When a complete array cannot connect to a primary scalar input, the runtime invokes the operation for each element.

The runtime processes elements sequentially in index order. It preserves that order in the output array.

```text
Array<T> -> operation(T -> R), where R is not Void
    => Array<R>
```

ShellLang 0.1 does not run lifted invocations concurrently.

### 11.2 Terminal scalar lifting

A lifted zero-output operation is terminal. The runtime never creates `Array<Void>`.

```text
Array<T> -> operation(T -> Void)
    => Void

Array<T> -> operation(T -> Result<Void,E>)
    => Result<Void,E>
```

The runtime invokes the operation sequentially in input order. An empty array performs zero invocations.

A non-fallible lift returns `Void` after all invocations complete. A fallible lift returns payloadless `Ok<Void>` after all invocations succeed.

The runtime stops a fallible lift at the first `Err`. It adds the complete array-index path to that error.

Terminal lifting collapses at every recursive array layer:

```text
Array<Array<T>> -> operation(T -> Void)
    => Void

Array<Array<T>> -> operation(T -> Result<Void,E>)
    => Result<Void,E>
```

Result propagation also preserves the terminal output:

```text
Result<Array<T>, E1> -> operation(T -> Void)
    => Result<Void,E1>

Result<Array<T>, E1> -> operation(T -> Result<Void,E2>)
    => Result<Void,common_error(E1,E2)>
```

Section 10.3 still controls secondary evaluation. The runtime evaluates secondary inputs once, including when the source array is empty.

### 11.3 Fallible value lifting

A fallible value-producing operation returns one Result around the output array:

```text
Array<T> -> operation(T -> Result<R,E>), where R is not Void
    => Result<Array<R>,E>
```

The runtime stops at the first `Err`. It does not invoke the operation for later elements.

The error context records the complete nested array-index path. The added context does not change the nominal error type.

Result and array adaptations compose recursively:

```text
Result<Array<T>, E1> -> operation(T -> Result<R, E2>)
    => Result<Array<R>, common_error(E1, E2)>
```

### 11.4 Runtime faults during lifting

A runtime fault stops the current lift immediately. The runtime does not invoke the operation for later elements.

The runtime adds the complete array-index path and source span to the fault. It then aborts the current compilation.

Completed invocations and their host effects remain committed.

## 12. Collection intrinsics

Collection intrinsics are compiler-defined generic operations. They are not registered commands.

The metadata service MUST expose them for help and completion.

### 12.1 Contextual element expressions

`where`, `sort`, `any`, `all`, `select`, and keyed `distinct` accept a contextual element expression. In that expression, `this` is the current array element and a leading `.` is shorthand for `this.`.

```text
players -> where(.health < 50)
players -> sort(by: .health)
players -> select(this.health)
```

The compiler gives each contextual element expression a lexical scope identity. The runtime evaluates it separately for each element. A nested intrinsic shadows and restores the nearest outer scope.

Command arguments, query arguments, explicit inputs, constructors, and value-taking collection intrinsics can reference an enclosing `this`. For a value-taking intrinsic that consumes an array directly, `this` is the whole array; the element-expression intrinsics listed above introduce their documented element scope instead.

If a contextual expression returns `Result<R,E>`, the intrinsic wraps its normal output in `Result<...,E>`. It stops at the first `Err` and adds the current index path. Contextual expressions run once per visited element in input order. Short-circuiting intrinsics do not visit later elements.

### 12.2 where

`where` has this type:

```text
where<T>(Array<T>, predicate: T -> Bool) -> Array<T>
```

The predicate can be positional or named:

```shelllang
find_entities(classname: "info_spawn") -> where(.spawn_order > 1)
find_entities(classname: "info_spawn") -> where(predicate: .spawn_order > 1)
```

`where` evaluates the predicate in input order. It preserves the order of accepted elements.

### 12.3 sort

`sort` has this type:

```text
sort<T,K>(Array<T>, by: T -> K) -> Array<T>
```

`K` MUST have a registered total order. The sort is stable and ascending.

`sort` evaluates each key once in input order before it sorts the elements.

### 12.4 take

`take` has this type:

```text
take<T>(Array<T>, count: Int32) -> Array<T>
```

A negative literal count is a static error. A negative runtime count is a runtime fault.

A zero count returns an empty array. A count greater than the input length returns the complete input array.

### 12.5 count

`count` has this type:

```text
count<T>(Array<T>) -> Int32
```

An empty array returns `0`. An array that exceeds the `Int32` count range causes a runtime fault.

### 12.6 first

`first` has this type:

```text
first<T>(Array<T>) -> Result<T, EmptyCollectionError>
```

An empty array returns `Err(EmptyCollectionError)`. A non-empty array returns its first element.

### 12.7 sum

`sum` accepts an array of one core numeric type. It returns the same numeric type.

An empty array returns that type's zero value. The runtime adds values from left to right.

Integer overflow is a runtime fault. Floating-point addition follows the .NET 10 numeric behavior.

### 12.8 min and max

`min` and `max` have these types:

```text
min<T>(Array<T>) -> Result<T, EmptyCollectionError>
max<T>(Array<T>) -> Result<T, EmptyCollectionError>
```

`T` MUST have a registered total order. Both intrinsics inspect values from left to right.

An empty array returns `Err(EmptyCollectionError)`.

Built-in floating-point ordering follows the .NET 10 `CompareTo` behavior. String ordering is ordinal.

### 12.9 average

`average` accepts an array of one core numeric type.

| Input | Success type |
| --- | --- |
| `Array<Int32>` | `Float64` |
| `Array<Int64>` | `Float64` |
| `Array<UInt32>` | `Float64` |
| `Array<UInt64>` | `Float64` |
| `Array<Float32>` | `Float32` |
| `Array<Float64>` | `Float64` |

The full return type is `Result<R,EmptyCollectionError>`.

An empty array returns `Err(EmptyCollectionError)`. Integer inputs convert to `Float64` before accumulation.

The runtime processes values from left to right.

### 12.10 at and last

```text
at<T>(Array<T>, index: Int32) -> T
last<T>(Array<T>) -> Result<T, EmptyCollectionError>
```

`at` uses zero-based indexing. A negative index is relative to the end, so `-1` selects the last element and `-length` selects the first. An index outside the array after normalization causes runtime fault `SL4006`.

`last` returns `Err(EmptyCollectionError)` for an empty array.

### 12.11 skip and slice

```text
skip<T>(Array<T>, count: Int32) -> Array<T>
slice<T>(Array<T>, start: Int32, count: Int32) -> Array<T>
```

`skip` returns the input without its first `count` elements. A count greater than the array length returns an empty array.

`slice` accepts an end-relative negative `start`. After normalization, the complete range from `start` through `start + count` MUST be within the array. `start == length` is valid only when `count == 0`. An invalid range causes runtime fault `SL4007`.

A negative literal count is a static error. A negative runtime count for either intrinsic causes runtime fault `SL4004`.

### 12.12 any and all

```text
any<T>(Array<T>, predicate: T -> Bool) -> Bool
all<T>(Array<T>, predicate: T -> Bool) -> Bool
```

`any` stops at the first `true`; an empty array returns `false`. `all` stops at the first `false`; an empty array returns `true`. A fallible predicate changes the output to `Result<Bool,E>`.

### 12.13 select

```text
select<T,R>(Array<T>, selector: T -> R) -> Array<R>
```

`select` evaluates the selector for every element and preserves input order. A fallible selector returns `Result<Array<R>,E>`. A selector that produces `Void` is a static error.

### 12.14 contains and concat

```text
contains<T>(Array<T>, value: T) -> Bool
concat<T>(Array<T>, other: Array<T>) -> Array<T>
```

`contains` uses the declared element type's registered equality. It is a static error when that equality is unavailable.

`concat` preserves the primary array's static type. The other array MUST be assignable to that type; array covariance is not used to widen the primary type. Both operations evaluate their supplied argument once.

### 12.15 distinct and reverse

```text
distinct<T>(Array<T>) -> Array<T>
distinct<T,K>(Array<T>, by: T -> K) -> Array<T>
reverse<T>(Array<T>) -> Array<T>
```

`distinct` preserves the first element for each equal value or key and retains input order. The selected equality MUST be registered. A fallible key returns `Result<Array<T>,E>`.

`reverse` returns a new immutable array with the opposite order.

### 12.16 single

```text
single<T>(Array<T>) -> Result<T, CollectionCardinalityError>
```

`single` succeeds only when the array contains exactly one element. Otherwise its error records the actual count. Filtering remains explicit through `where`; `single` has no predicate form.

### 12.17 Intrinsic argument rules

Intrinsic arguments can be positional or named. Positional arguments MUST precede named arguments. Unknown names, duplicate arguments, missing required arguments, and explicit input syntax are static errors.

Collection intrinsics accept arrays reached through Result propagation or default-output projection. A fallible contextual output and an outer Result combine their error types by the standard nearest-common-error rule.

## 13. Program execution

The runtime executes statements in source order.

An expression statement exposes its value to the host. A script's result is its final expression value, or `Void` when it has no final expression value.

An assignment statement returns `Void` to the host after it commits the binding.

A runtime fault or host fault stops execution immediately. The runtime returns the fault and the source span of the failing operation.

The runtime does not undo earlier assignments or host command effects.

## 14. Diagnostics

The compiler MUST reject invalid source before execution. It MUST NOT execute a partial compilation.

Each diagnostic MUST contain:

- A stable diagnostic code
- A severity
- A source span
- A short message
- Related command, port, argument, member, or type names when applicable

A connection diagnostic SHOULD also contain:

- The actual type
- The expected type
- Each attempted adaptation layer
- The endpoint command and parameter

Example:

```text
SL2004 Cannot connect Int32 to kill.target : Entity.

  player.health -> kill
  ^^^^^^^^^^^^^    ^^^^^^^^^^^

Attempted:
  whole value: Int32 is not assignable to Entity
  result propagation: not applicable
  default output: not available
  array lifting: not applicable
```

A runtime error from lifted evaluation SHOULD include its array-index path.

## 15. Exclusions

ShellLang 0.1 does not include these features:

- User functions or lambdas
- Blocks or general control flow
- Loops
- Async commands
- Executable streams
- Direct property mutation
- Reflection-based exposure
- User-defined types
- Modules or imports
- OS process execution
- Inter-process transport
- Shared-memory transport
- Concurrent lifting
- `--argument` command-line syntax
- Implicit numeric conversion for typed values
- String-to-number parsing or Boolean/numeric conversion
- Runtime casts or type tests

These exclusions are compatibility boundaries for version 0.1. Later specifications can add them without changing the rules in this document.

## Appendix A. Normative examples

### A.1 Exact collection consumer

Given:

```text
players : Array<Player>
sum : Array<Int32> -> Int32
print : Any -> Void
```

This pipeline invokes `sum` once:

```text
players.health -> sum -> print
```

### A.2 Scalar command lifting

Given:

```text
damage
input:
    target : Player default
argument:
    amount : Int32
output:
    result : DamageResult
error:
    DamageError
```

This pipeline invokes `damage` once per player:

```text
players -> damage(amount: 10)
```

Its type is `Result<Array<DamageResult>,DamageError>`.

### A.3 Result member propagation

Given `players -> first : Result<Player,EmptyCollectionError>`, this expression has type `Result<Int32,EmptyCollectionError>`:

```text
(players -> first).health
```

### A.4 Failed rebinding

```text
x = "hello"
x = something -> require
x + 10
```

If `require` faults, execution stops at the second statement. The old `x : String` binding remains in the session.

The third statement does not execute, even though the compiler typed its `x` reference as the second assignment's result type.

### A.5 Ordinary argument evaluation

```text
players -> damage(amount: random_damage())
```

The runtime calls `random_damage()` once. It reuses the value for every player.

### A.6 Contextual evaluation

```text
players -> where(.health < random_limit())
```

The full predicate is contextual. The runtime therefore calls `random_limit()` once per player.

### A.7 Output record precedence

Given a `Trace.Output` record with default field `entity`:

```text
trace_result -> inspect_trace # Pass Trace.Output.
trace_result -> kill          # Pass trace_result.entity.
trace_result.hit -> display   # Pass the explicit hit field.
```

### A.8 Empty reducers

```shelllang
[1] -> skip(1) -> count   # 0.
[1] -> skip(1) -> sum     # Numeric zero.
[1] -> skip(1) -> first   # Err(EmptyCollectionError).
[1] -> skip(1) -> min     # Err(EmptyCollectionError).
[1] -> skip(1) -> max     # Err(EmptyCollectionError).
[1] -> skip(1) -> average # Err(EmptyCollectionError).
```

### A.9 Non-fallible terminal lifting

Given `kill : Player -> Void`, this statement kills each player in index order and then produces `Void`:

```text
players -> kill
```

An empty `players` array performs no invocations and still produces `Void`.

### A.10 Fallible terminal lifting

Given `register : Player -> Result<Void,RegistrationError>`, this statement returns payloadless `Ok<Void>` after complete success:

```text
players -> register
```

The first `Err` stops the lift. The error contains the failing player's array index.

An empty array returns payloadless `Ok<Void>`.

### A.11 Recursive terminal lifting

These types show terminal collapse through nested arrays and an outer Result:

```text
Array<Array<Player>> -> kill
    => Void

Result<Array<Player>,LoadError> -> kill
    => Result<Void,LoadError>
```

Neither expression creates `Array<Void>`.

### A.12 Command runtime fault

Given a lifted command that declares `GAME1001`, a returned `GAME1001` fault stops the current lift and compilation.

The runtime records the failing array index. It does not invoke the command for later elements.
