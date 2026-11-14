-- WolverineFx.Postgresql 6.64.0 declares one more index than 6.60.0 did (2026110401): the scheduled envelopes by their time, for the
-- poll of due messages. Wolverine runs with AutoCreate.None, so the migrations are the one source of its schema
-- (JobGateATests.A6 compares them with what the installed Wolverine expects). Unqualified, as 2026110401.
CREATE INDEX IF NOT EXISTS idx_wolverine_incoming_envelopes_scheduled ON wolverine_incoming_envelopes USING btree (execution_time) WHERE (status = 'Scheduled');
