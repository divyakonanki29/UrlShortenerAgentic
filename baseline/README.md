# Brownfield baseline (v1)

A frozen snapshot of the URL shortener as it existed *before* the brownfield
reliability change. It builds and runs, and it has real defects: a shared
`Random`, a check-then-act race on code reservation, lost click updates, and
no scheme allow-list on redirect targets.

`--scenario=brownfield` treats this folder as the existing codebase:

1. **Requirements** analyses it (`CodebaseAnalyzer`) and writes
   `docs/brownfield/impact-analysis.md`: the API surface, data flow, and findings with
   file:line evidence, plus which files are in scope.
2. **Design** turns the findings into a change plan.
3. **Implementation** checks the baseline out into `generated/UrlShortener.Api`,
   changes only the files that need it, and writes the patch to
   `docs/brownfield/changes.diff`.
4. Its exit gate enforces the scope (CHG-004) and re-analyses the result to
   confirm every finding is gone (CHG-005).

Do not edit these files to "fix" them. They are the input to the scenario.
