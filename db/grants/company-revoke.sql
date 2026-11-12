-- Runs on each Optima company database an administrator cleared. Dropping the user removes its grants, and nothing
-- else on the company database.
IF USER_ID(N'$ServiceLogin$') IS NOT NULL
    EXEC (N'DROP USER [$ServiceLogin$];');
