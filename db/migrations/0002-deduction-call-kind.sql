-- An allowance is written to Optima exactly like a deduction, so its calls share the table; the kind tells them apart.
-- Rows written before this migration were deductions.
ALTER TABLE integrator.deduction_call
    ADD kind varchar(10) NOT NULL
        CONSTRAINT df_deduction_call_kind DEFAULT ('Deduction')
        CONSTRAINT ck_deduction_call_kind CHECK (kind IN ('Deduction', 'Allowance'));
