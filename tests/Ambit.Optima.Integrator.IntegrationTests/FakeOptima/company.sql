-- A stand-in for an Optima company database. Tables carry only the columns the integrator touches, and the procedures
-- copy the behaviour that matters to it: identities, import keys, output parameters, and an error raised without RETURN.
CREATE SCHEMA CDN;
GO
CREATE TABLE CDN.Firma (Fir_Numer int NOT NULL, Fir_Wartosc nvarchar(400) NULL);
CREATE TABLE CDN.PracKod
(
    PRA_PraId int IDENTITY PRIMARY KEY,
    PRA_Kod nvarchar(20) NOT NULL,
    PRA_ImportRowId varchar(36) NULL
);
CREATE TABLE CDN.PracEtaty
(
    PRE_PreId int IDENTITY PRIMARY KEY,
    PRE_PraId int NOT NULL,
    PRE_Nazwisko nvarchar(40) NOT NULL,
    PRE_DataUr datetime NULL,
    PRE_Plec nvarchar(1) NULL,
    PRE_Obywatelstwo nvarchar(30) NULL,
    PRE_ObywatelKodKraju nvarchar(2) NULL,
    PRE_MLDKraj nvarchar(40) NULL,
    PRE_MLDKodKraju varchar(2) NULL,
    PRE_DowOsobisty nvarchar(12) NULL,
    PRE_Paszport nvarchar(9) NULL,
    PRE_DowWaznyDo datetime NULL,
    PRE_ZatrudnionyDo datetime NULL
);
CREATE TABLE CDN.DaneKadMod (DKM_DkmId int PRIMARY KEY, DKM_Rodzaj int NOT NULL, DKM_Nazwa nvarchar(100) NOT NULL);
CREATE TABLE CDN.TypWyplata
(
    TWP_TwpId int PRIMARY KEY,
    TWP_Nazwa nvarchar(40) NOT NULL,
    TWP_Skrot nvarchar(40) NULL,
    TWP_RodzajZrodla int NOT NULL,
    TWP_Standardowy smallint NULL,
    TWP_AlgKwota decimal(15, 2) NULL,
    TWP_PKZP tinyint NULL,
    TWP_ZajecieWynagr tinyint NULL,
    TWP_ImportRowId varchar(36) NULL,
    TWP_ImportRowId2 varchar(36) NULL,
    TWP_AlgPotracenie tinyint NOT NULL,
    TWP_Nieaktywny tinyint NULL,
    TWP_Wycofany tinyint NULL
);
CREATE TABLE CDN.ListyPlac
(
    LPL_LplId int PRIMARY KEY,
    LPL_NumerPelny nvarchar(30) NULL,
    LPL_Nazwa nvarchar(128) NOT NULL,
    LPL_DataOd datetime NOT NULL,
    LPL_DataDo datetime NOT NULL,
    LPL_DataDok datetime NOT NULL,
    LPL_Zamknieta datetime NULL,
    LPL_Rok int NOT NULL,
    LPL_Miesiac smallint NOT NULL
);
CREATE TABLE CDN.Wyplaty
(
    WPL_WplId int PRIMARY KEY,
    WPL_LplId int NOT NULL,
    WPL_PraId int NOT NULL,
    WPL_Brutto decimal(15, 2) NOT NULL,
    WPL_Wartosc decimal(15, 2) NOT NULL,
    WPL_Zamknieta tinyint NOT NULL
);
CREATE TABLE CDN.WypElementy
(
    WPE_WpeId int PRIMARY KEY,
    WPE_WplId int NOT NULL,
    WPE_TwpId int NULL,
    WPE_Nazwa nvarchar(128) NOT NULL,
    WPE_Wartosc decimal(15, 2) NOT NULL
);
CREATE TABLE CDN.Dodatki
(
    DOD_DodId int IDENTITY PRIMARY KEY,
    DOD_PraId int NOT NULL,
    DOD_ImportRowId varchar(36) NOT NULL UNIQUE,
    DOD_Nazwa nvarchar(40) NOT NULL
);
CREATE TABLE CDN.DodatkiHist
(
    DOH_DohId int IDENTITY PRIMARY KEY,
    DOH_DodId int NOT NULL,
    DOH_ImportRowId varchar(36) NOT NULL UNIQUE,
    DOH_Wartosc1 decimal(15, 2) NOT NULL,
    DOH_WaznyOd datetime NOT NULL,
    DOH_WaznyDo datetime NOT NULL,
    DOH_TWP_ImportRowId varchar(36) NULL
);
GO
CREATE PROCEDURE CDN.DodajPracownika
    @OnlyNew int, @ConfigDBName nvarchar(128), @ServerName nvarchar(128),
    @PRA_ImportRowId nvarchar(36), @PRA_Kod nvarchar(20), @PRA_Archiwalny int,
    @PRE_Nazwisko nvarchar(40), @PRE_Imie1 nvarchar(30), @PRE_Imie2 nvarchar(30), @PRE_ImieOjca nvarchar(30),
    @PRE_ImieMatki nvarchar(30), @PRE_DataUr nvarchar(20), @PRE_MiejsceUr nvarchar(40), @PRE_NazwRodowe nvarchar(40),
    @PRE_NazwRodoweM nvarchar(40), @PRE_Pesel nvarchar(11), @PRE_NipKraj nvarchar(2), @PRE_NipE nvarchar(13),
    @Status nvarchar(40), @PRE_MLDKraj nvarchar(40), @PRE_MLDWojewodztwo nvarchar(40), @PRE_MLDPowiat nvarchar(40),
    @PRE_MLDGmina nvarchar(40), @PRE_MLDUlica nvarchar(40), @PRE_MLDNrDomu nvarchar(10), @PRE_MLDNrLokalu nvarchar(10),
    @PRE_MLDMiasto nvarchar(40), @PRE_MLDKodPocztowy nvarchar(10), @PRE_MLDPoczta nvarchar(40),
    @PRE_MLDAdres2 nvarchar(40), @PRE_HDKTelefon1 nvarchar(20), @PRE_ZatrudnionyOd nvarchar(20),
    @PRE_PODMnoznikKoszty int, @PRE_PODMnoznikUlga int,
    @Urz_Akronim nvarchar(20), @Urz_ImportRowId nvarchar(36), @Fpl_Nazwa nvarchar(40), @Fpl_ImportRowId nvarchar(36),
    @Kat_KodSzczegol nvarchar(20), @Kat_ImportRowId nvarchar(36), @PRE_KategoriaOpis nvarchar(40),
    @BNa_Numer nvarchar(20), @BNa_ImportRowId nvarchar(36), @PRE_RachunekNr nvarchar(50), @PRE_IBAN int,
    @PRE_HDKUwagi nvarchar(1024), @PRE_NieRozliczac int, @PRE_RodzajDokumentu int,
    @PRE_KontoKsiegowe nvarchar(50), @PRE_KontoKsiegowe2 nvarchar(50), @PRE_ETAEtatL int, @PRE_ETAEtatM int,
    @Zak_Symbol nvarchar(20), @Zak_ImportRowId nvarchar(36), @ZrdId nvarchar(20), @PRE_HDKEmail nvarchar(127),
    @CntId int, @StanowiskoId int, @OpeId int,
    @SlowId int OUTPUT, @Warnings nvarchar(1000) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PraId int;

    -- Like Comarch's procedure: an error, and no RETURN after it.
    IF @PRE_Nazwisko = N'Fail'
        RAISERROR (N'Błąd dla PESEL %s#OPT0042#', 16, 1, @PRE_Pesel);

    INSERT CDN.PracKod (PRA_Kod, PRA_ImportRowId) VALUES (@PRA_Kod, @PRA_ImportRowId);
    SET @PraId = SCOPE_IDENTITY();
    INSERT CDN.PracEtaty (PRE_PraId, PRE_Nazwisko, PRE_DataUr, PRE_MLDKraj, PRE_Plec)
    VALUES (@PraId, @PRE_Nazwisko, CONVERT(datetime, NULLIF(@PRE_DataUr, N''), 112), @PRE_MLDKraj, N'M');
    SET @SlowId = SCOPE_IDENTITY();
    SET @Warnings = IIF(@PRE_Pesel = N'', NULL, N'PESEL ' + @PRE_Pesel + N' nieprawidłowy#OPT0007#');
END;
GO
CREATE PROCEDURE CDN.ImportDataDodatki
    @PRA_PraId int, @OnlyNew int, @ConfigDBName nvarchar(128), @ServerName nvarchar(128),
    @DOD_ImportRowId varchar(36), @DOD_Nazwa nvarchar(40), @RDZ_ImportRowId varchar(36), @DOD_Rodzaj int,
    @DOD_Nieaktywny int, @DOD_Umowa int, @DOD_WliczChorWOkresie int, @ZrdId nvarchar(20), @OpeId int,
    @IdParam int OUTPUT, @Warnings nvarchar(1000) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM CDN.Dodatki WHERE DOD_ImportRowId = @DOD_ImportRowId)
        RAISERROR (N'Import key exists.', 16, 1);
    INSERT CDN.Dodatki (DOD_PraId, DOD_ImportRowId, DOD_Nazwa) VALUES (@PRA_PraId, @DOD_ImportRowId, @DOD_Nazwa);
    SET @IdParam = SCOPE_IDENTITY();
END;
GO
CREATE PROCEDURE CDN.ImportDataDodatkiHist
    @DOD_DodId int, @OnlyNew int, @ConfigDBName nvarchar(128), @ServerName nvarchar(128),
    @DOH_ImportRowId varchar(36), @TWP_ImportRowId varchar(36), @TWP_ImportRowId2 varchar(36),
    @DOH_ListaPlacSymbol nvarchar(20), @DOH_Nazwa nvarchar(40),
    @DOH_OkresOd_VCH varchar(24), @DOH_OkresDo_VCH varchar(24), @DOH_WaznyOd_VCH varchar(24), @DOH_WaznyDo_VCH varchar(24),
    @DOH_Wartosc1 decimal(15, 2), @DOH_Wartosc2 decimal(15, 2), @DOH_PozyczkaNr nvarchar(20), @DOH_Opis nvarchar(100),
    @DOH_OddelegowanyWaluta nvarchar(3), @DOH_PlatnoscZgodnaPrac int, @ZrdId nvarchar(20), @OpeId int,
    @IdParam int OUTPUT, @Warnings nvarchar(1000) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT CDN.DodatkiHist (DOH_DodId, DOH_ImportRowId, DOH_Wartosc1, DOH_WaznyOd, DOH_WaznyDo, DOH_TWP_ImportRowId)
    VALUES (@DOD_DodId, @DOH_ImportRowId, @DOH_Wartosc1, CONVERT(datetime, @DOH_WaznyOd_VCH, 120),
            CONVERT(datetime, @DOH_WaznyDo_VCH, 120), @TWP_ImportRowId);
    SET @IdParam = SCOPE_IDENTITY();
END;
GO
INSERT CDN.DaneKadMod (DKM_DkmId, DKM_Rodzaj, DKM_Nazwa) VALUES (1, 1, N'Magazynier');
INSERT CDN.TypWyplata
    (TWP_TwpId, TWP_Nazwa, TWP_Skrot, TWP_RodzajZrodla, TWP_Standardowy, TWP_AlgKwota, TWP_PKZP, TWP_ZajecieWynagr,
     TWP_ImportRowId, TWP_ImportRowId2, TWP_AlgPotracenie, TWP_Nieaktywny, TWP_Wycofany)
VALUES
    (338, N'Potrącenie za mieszkanie ', N'pot.mieszkanie', 4, 0, 0.00, 0, 0,
     '8468F49B-C16E-4529-8197-D94D93DDADD8', '6E5E45D1-E075-4CCC-8209-17FE740CDF41', 1, 0, 0),
    (339, N'Dodatek stażowy', NULL, 4, 12, 150.00, 0, 0, 'A0000000-0000-4000-8000-000000000339',
     'B0000000-0000-4000-8000-000000000339', 0, 0, 0),
    (340, N'Stare potrącenie', NULL, 4, NULL, NULL, 0, 0, NULL, NULL, 1, 1, 0);
-- Payroll lists. The October list is declared in November (LPL_Rok / LPL_Miesiac); a December list is left out of a
-- November read. Employees 9001 and 9002 stand for people the tests do not add: one with the HR application's id, one
-- without.
SET IDENTITY_INSERT CDN.PracKod ON;
INSERT CDN.PracKod (PRA_PraId, PRA_Kod, PRA_ImportRowId) VALUES
    (9001, N'LISTA1', 'C0000000-0000-4000-8000-000000000001'),
    (9002, N'LISTA2', NULL);
SET IDENTITY_INSERT CDN.PracKod OFF;
INSERT CDN.ListyPlac (LPL_LplId, LPL_NumerPelny, LPL_Nazwa, LPL_DataOd, LPL_DataDo, LPL_DataDok, LPL_Zamknieta, LPL_Rok,
                      LPL_Miesiac) VALUES
    (41, N'EtatLP/10/2026', N'Etat', '20261001', '20261031', '20261110', '20261110', 2026, 11),
    (42, N'UCPLP/11/2026', N'', '20261101', '20261130', '20261130', NULL, 2026, 11),
    (43, N'EtatLP/12/2026', N'Etat', '20261201', '20261231', '20261231', NULL, 2026, 12);
INSERT CDN.Wyplaty (WPL_WplId, WPL_LplId, WPL_PraId, WPL_Brutto, WPL_Wartosc, WPL_Zamknieta) VALUES
    (901, 41, 9001, 6500.00, 4712.34, 1),
    (902, 41, 9002, 4806.00, 3773.85, 1),
    (903, 43, 9001, 6500.00, 4712.34, 0);
INSERT CDN.WypElementy (WPE_WpeId, WPE_WplId, WPE_TwpId, WPE_Nazwa, WPE_Wartosc) VALUES
    (5001, 901, 339, N'Dodatek stażowy', 150.00),
    (5002, 901, NULL, N'Wynagrodzenie zasadnicze', 6350.00),
    (5003, 902, NULL, N'Wynagrodzenie zasadnicze', 4806.00);
