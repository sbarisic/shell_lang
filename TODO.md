# TODO

- [ ] Replace raw CLR `ToString()` value rendering with one shared descriptor-aware formatter for both REPL results and `print`. Format primitives, arrays, and Results recursively; expose only registered ShellLang members (including inherited members); never use CLR reflection; treat host types without registered members as opaque type names; exclude queries and commands; and handle cycles, depth limits, and member-getter failures safely.
