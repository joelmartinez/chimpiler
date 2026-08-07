---
name: kb-interactive-evaluation
description: Evaluate compiled KB GraphRAG with isolated corpus-builder and answerer agents.
---

# KB interactive evaluation

Use this skill to evaluate whether `chimpiler kb` lets an agent answer a multi-document question
without reading the source corpus directly.

## Protocol

1. Build the CLI with `dotnet build src/Chimpiler/Chimpiler.csproj`.
2. Launch a **corpus-builder** general subagent. Give it the compiled assembly path and direct it
   to create a fresh corpus and database under a unique directory in `/tmp`. It must:
   - create at least three short documents containing an alias and an explicit entity relationship;
   - run `chimpiler kb prompt`, install the default local model, initialize the database, and index
     the corpus;
   - use `kb entity` and `kb relate` with exact source evidence to record the alias and relationship;
   - choose a question whose answer requires traversing those agent-authored graph facts;
   - return the database path, question, expected answer, and success criteria to the orchestrator.
3. Launch a separate **answerer** general subagent. It receives only the compiled assembly path,
   database path, question, and `chimpiler kb prompt` output. Explicitly forbid it from opening,
   listing, searching, or otherwise reading the corpus directory or source documents. It may use
   only `chimpiler kb` commands against the supplied database.
4. Require the answerer to state the answer, the CLI commands it used, and source paths returned
   by the CLI. It must distinguish direct hits from `(graph)` results and not present a candidate
   alias as proof without source evidence. It may use graph traversal, but must not add facts.
5. Compare the answerer's answer and cited evidence with the corpus-builder's expected answer.
   Report the commands, graph depth, sources, pass/fail result, and any failure mode.

## Guardrails

- Keep embeddings local: use `--model default`; do not send corpus text to external services.
- Use a new temporary directory and database per evaluation; never reuse a production KB.
- The orchestrator may inspect builder output for evaluation, but the answerer must not receive the
  expected answer or corpus contents.
- Clean up only the exact temporary directory created for this evaluation after results are recorded.
