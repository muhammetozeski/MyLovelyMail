# Maintaining project knowledge

Documentation preserves project-specific decisions and verified solutions so the next developer does not repeat the investigation. General framework tutorials, raw tool output and task progress belong elsewhere.

Before adding a document, identify the concrete development task it helps and the information that cannot be recovered cheaply from the code. Extend the existing topic instead of writing a second copy. Add links to the nearest index and update links when files move.

Describe the conditions, expected result and evidence behind a solution. A source-code observation is not a runtime test. An earlier test applies only while its relevant code and conditions remain unchanged. Do not present an untested method as a verified procedure.

Keep temporary output outside the source tree. Put chronological context in Walkthrough records and durable contracts in these documents. Do not turn a one-time user request into a permanent project rule or record an approval the user did not give.

When behavior changes, update the document that owns that explanation. When no reusable knowledge changes, no new document is needed. Check the links and claims touched by the work; avoid unrelated documentation maintenance.
