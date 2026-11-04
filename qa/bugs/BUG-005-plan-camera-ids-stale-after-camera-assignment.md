# BUG-005: plan camera ids stale after camera assignment

- Severity: minor
- Page: Pipelines (Active list, plan header)
- Suspected component: registry / planner

## Steps to reproduce
Create a camera with plan_id = the active plan (Sources dialog or `POST /api/cameras`), then open Pipelines.

## Expected
Plan shows 21 cameras / `camera_ids` includes the new camera.

## Actual
The camera runs that plan (executor uses camera.plan_id) but `GET /api/plans/{id}` `camera_ids` still lists 20 and the UI says '20 cameras'.

## Evidence
Repro in t12 preamble (curl POST with plan_id, then GET plan).

## Hint
plan.camera_ids is only written by the planner on activation; registry plan_id changes do not update it. Derive from cameras in the UI or have the planner follow `camera.config`.
