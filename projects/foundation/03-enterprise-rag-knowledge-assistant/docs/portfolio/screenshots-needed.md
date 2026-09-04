# Screenshots to capture

For the portfolio page and interview handout. All should be captured from the
running local instance and pasted into `docs/portfolio/screenshots/` (folder
intentionally not committed empty — add real images as you take them).

1. **Swagger / OpenAPI page** at `http://localhost:5003/openapi/v1.json`
   (or view the JSON in a browser add-on). Shows the whole API surface.

2. **Grounded answer response.** Postman or Insomnia against
   `POST /api/v1/query` with an HR PTO question. Highlight the `[1]`
   citation marker in the answer and the `citations[]` list on the right.

3. **Refusal response.** Same endpoint with the LTIP question as an
   unauthorised employee. Highlight `refused: true`,
   `refusalReason: "insufficient-retrieval"`, and the empty
   `citations` array.

4. **Eval console output.** Terminal running
   `dotnet run --project src\RagAssistant.Eval` with the three-mode table
   visible.

5. **Test run terminal.** `dotnet test -c Release` with the
   `Passed! Failed: 0, Passed: 66, ...` summary highlighted.

6. **OpenTelemetry console spans.** A snippet from the running API showing
   the `rag.retrieve` and `rag.generate` spans with attributes.
