# Work handover

Use dated Markdown records to preserve the current context and experience that changes the next development decision. Git already records code changes; these records explain observations, decisions and outcomes that are otherwise lost.

1. Select records by filename before reading their contents. Read the newest context and the newest relevant accumulation record, if one exists, then select relevant later experience by title. Stop once the task context is understood.
2. Reuse information already read in this session. Do not scan unrelated conversations, repositories or historical records to fill a handover.
3. Add a new record when an observation or result affects future work, including before leaving unfinished work. Use the actual local timestamp and a descriptive title: `yyyy.MM.dd HH.mm.ss - Observed problem and resulting decision.md`.
4. Write the current application context, the observation that changed the approach, its outcome and its implication for the next developer. Link to durable documentation rather than copying it. Distinguish source inspection, runtime results and unresolved questions.
5. Correct an earlier conclusion in a new record that names the changed conclusion and its evidence. Do not silently rewrite old experience. Accumulate earlier experience only when that reduces future reading; preserve the lessons that still matter.

Keep records as short as their meaning allows. Avoid empty sections, code inventories, command logs and repeated summaries. This economy applies to Walkthrough itself, not to implementation or verification work.
