# Runbook: Blameless Postmortem Process

## Create
After a resolved incident, create the templated postmortem. Include the automatic impact attribution, factual timeline, contributing factors, what went well, and what made recovery harder. Describe system conditions and decisions rather than assigning personal blame.

## Review state
`Draft → InReview → Approved → Published` is enforced. Reviewers check factual accuracy, action quality, and whether the proposed fixes address mechanism rather than symptoms.

## Action items
Every action needs a specific description, owner, and due date. Complete items only when verifiable work is finished. Use `/api/v1/postmortems/actions/overdue` in operational reviews.

## Learning
Use `/api/v1/postmortems/themes` to identify recurring factors. Recurrence is a signal to improve platform controls, runbooks, training, or investment prioritization; it is not evidence to blame individuals.
