-- Runs on master. The Worker signs in with the Windows identity of its service.
IF SUSER_ID(N'$ServiceLogin$') IS NULL
    EXEC (N'CREATE LOGIN [$ServiceLogin$] FROM WINDOWS;');
