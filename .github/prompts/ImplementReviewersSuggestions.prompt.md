---
name: ImplementReviewersSuggestions
description: Implement reviewer suggestions for the current PR with critical evaluation, full validation, and reviewer follow-up.
---
You are helping implement reviewer feedback on the currently checked-out pull request.

Goal: address all actionable reviewer suggestions with high quality, preserve project conventions, and leave a clear audit trail in review replies.

Process:

1) Gather all review feedback for this PR
- Fetch all reviewer comments, especially inline comments and unresolved threads.
- Include both requested changes and non-blocking suggestions.
- Build a checklist of unique actionable items (deduplicate repeated comments).

2) Evaluate each suggestion critically
- Do not assume reviewer feedback is always correct.
- For each item, inspect relevant code, tests, and surrounding architecture.
- Decide one of:
	- Accept as-is
	- Accept with adjusted implementation
	- Reject with technical rationale

3) Implement with project-best solution
- If the reviewer suggestion is good and aligns with repo standards, implement it.
- If intent is good but implementation is suboptimal, implement a better alternative and document why.
- Keep changes minimal, focused, and consistent with existing patterns.

4) Validate thoroughly after changes
- Run `dotnet build` and ensure no compiler/analyzer warnings or errors are introduced.
- Run `dotnet test` (full suite).
- Run smoke test: `dotnet run -- --server localhost --prompt "how is this computer doing?"`.
- If anything fails, fix and re-run until clean.

5) Iterate until stable
- Continue cycling: implement -> test/build/run -> fix.
- Do not stop with partial fixes or known breakage.

6) Finalize code changes
- Stage relevant files only.
- Create a clear commit message summarizing what was addressed.
- Push branch updates to remote.

7) Reply to every inline review comment
- Prefer posting inline replies with `mcp_github2_add_comment_to_pending_review` as part of a pending review workflow.
- If MCP comment posting fails, fall back to GitHub CLI (`gh`) to post equivalent review feedback.
- If needed, use `mcp_github2_pull_request_review_write` to create/submit the pending review around inline comments.
- Respond to each comment with what was changed or why no change was made.
- Keep replies concise, technical, and respectful.
- When rejecting or altering a suggestion, explain tradeoffs and project-specific constraints.

Output expectations:
- Provide a short completion summary with:
	- Implemented suggestions
	- Declined suggestions and rationale
	- Validation results (tests/build/smoke)
	- Commit SHA(s) and pushed branch
	- Confirmation that all inline comments were replied to