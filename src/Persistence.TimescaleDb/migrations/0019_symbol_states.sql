-- ADR-0027: a symbol derives a named state from its tag, and the mapping is a list of declarative
-- comparisons held on the component.
--
-- `states` is jsonb and holds an array, empty or absent for every kind that is not a symbol:
--
--   [{"state":"running","when":"equals","value":"true"},
--    {"state":"stopped","when":"equals","value":"false","otherwise":true}]
--
-- Why a document rather than a table of its own. A mapping is **one value of one component**: it has
-- no identity, nothing references a rule, and no query ever wants one rule without the rest — so a
-- child table would add a second write path, a second set of delete semantics and a second thing for
-- the seeder to get out of step with, to buy nothing. It is also bounded by construction: a symbol has
-- a fixed handful of states, so "a JSON column is where structure goes to hide" does not apply here
-- the way it would to a table of anything.
--
-- The order of the array is meaning — first match wins — and jsonb does not preserve order in
-- general. It does preserve the order of an array's elements, which is what this is, so the mapping is
-- written and read back whole and the order survives. That is worth stating because it is the one
-- property of this column that is load-bearing.

ALTER TABLE screen_component
    ADD COLUMN states jsonb NOT NULL DEFAULT '[]'::jsonb;

-- Which drawing, for a symbol. Separate from `kind` because they are separate questions: the kind
-- says "this component is a picture of equipment", and this says which picture. That separation is
-- what makes "a mapping naming a state this drawing does not have" a question that can be asked at
-- all — a mapping is only valid against a particular drawing (ADR-0027 §6).
ALTER TABLE screen_component
    ADD COLUMN symbol text;

-- A mapping is an array of objects, or nothing. Not merely "is jsonb": `"hello"` and `{"a":1}` are
-- both valid jsonb and neither is a mapping, and a shape error here would surface as a component that
-- silently draws `unknown` forever.
ALTER TABLE screen_component
    ADD CONSTRAINT ck_screen_component_states_is_an_array
        CHECK (jsonb_typeof(states) = 'array');

-- The screen's own read view has to carry the columns, for the reason migration 0018 gives about
-- alarm_definition_active: a view names its columns, so a column added to the table is invisible
-- through it until it is added here — and a symbol read without its mapping would draw `unknown` on
-- every screen while looking like it had been configured.
--
-- Recreated rather than altered, which is the only way Postgres offers.
DROP VIEW screen_component_active;

CREATE VIEW screen_component_active AS
SELECT id, site_id, screen_id, row_index, column_span, position, kind, title, tag_id, device_id,
       symbol, states
FROM screen_component
WHERE deleted_at IS NULL;
