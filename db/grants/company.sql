-- Runs on each Optima company database an administrator selected: the least the Worker needs there, column by column.
IF OBJECT_ID(N'CDN.DodajPracownika') IS NULL OR OBJECT_ID(N'CDN.PracEtaty') IS NULL
    THROW 51402, N'This is not an Optima company database.', 1;
GO
IF USER_ID(N'$ServiceLogin$') IS NULL
    EXEC (N'CREATE USER [$ServiceLogin$] FOR LOGIN [$ServiceLogin$];');
GO
-- Employees: Comarch's procedure adds one, and the Worker completes the columns it has no parameter for.
GRANT EXECUTE ON OBJECT::CDN.DodajPracownika TO [$ServiceLogin$];
GRANT SELECT ON CDN.Firma (Fir_Numer, Fir_Wartosc) TO [$ServiceLogin$];
GRANT SELECT ON CDN.PracKod (PRA_PraId, PRA_Kod, PRA_ImportRowId) TO [$ServiceLogin$];
GRANT SELECT ON CDN.DaneKadMod (DKM_DkmId, DKM_Rodzaj, DKM_Nazwa) TO [$ServiceLogin$];
GRANT SELECT ON CDN.PracEtaty (PRE_PreId, PRE_PraId) TO [$ServiceLogin$];
GRANT UPDATE ON CDN.PracEtaty (
    PRE_Plec, PRE_Obywatelstwo, PRE_ObywatelKodKraju, PRE_MLDKodKraju,
    PRE_DowOsobisty, PRE_Paszport, PRE_DowWaznyDo, PRE_ZatrudnionyDo
) TO [$ServiceLogin$];
-- Payroll components: deductions and allowances.
GRANT SELECT ON CDN.TypWyplata (
    TWP_TwpId, TWP_Nazwa, TWP_Skrot, TWP_RodzajZrodla, TWP_Standardowy, TWP_AlgKwota, TWP_PKZP, TWP_ZajecieWynagr,
    TWP_ImportRowId, TWP_ImportRowId2, TWP_AlgPotracenie, TWP_Nieaktywny, TWP_Wycofany
) TO [$ServiceLogin$];
-- Deductions: Comarch's import procedures write the rows, and the Worker reads only the keys. An Optima version without
-- these procedures still gets the rights above.
IF OBJECT_ID(N'CDN.ImportDataDodatki') IS NOT NULL AND OBJECT_ID(N'CDN.ImportDataDodatkiHist') IS NOT NULL
BEGIN
    GRANT EXECUTE ON OBJECT::CDN.ImportDataDodatki TO [$ServiceLogin$];
    GRANT EXECUTE ON OBJECT::CDN.ImportDataDodatkiHist TO [$ServiceLogin$];
    GRANT SELECT ON CDN.Dodatki (DOD_DodId, DOD_PraId) TO [$ServiceLogin$];
    GRANT SELECT ON CDN.DodatkiHist (DOH_DohId, DOH_DodId, DOH_ImportRowId) TO [$ServiceLogin$];
END;
