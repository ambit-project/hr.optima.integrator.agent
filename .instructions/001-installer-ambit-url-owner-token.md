# Zadanie 001: Adres Ambit i token w instalatorze agenta, token Optimy wystawiany organizacji przez Ownera

Dwa repo, oba na tej maszynie (Windows Server 2022, SQL Server z Optimą, Inno Setup 6):

- `C:\Users\Administrator\projekty\ambit.optima.agent`: agent (.NET 10, Windows Service, Flutter UI, instalator Inno Setup).
- `C:\Users\Administrator\projekty\ambit.es`: Ambit System (.NET 10, Marten + Wolverine, front TanStack Start + HeroUI v3). Przeczytaj jego `CLAUDE.md`.

Użyj skilli: `dotnet10-standard`, `dotnet10-feature-delivery`, `dotnet10-operability`, `tanstack-heroui-frontend`, `find-docs`.
Konwencje: conventional commits, małe commity, które się budują; kod, komentarze i ADR po angielsku; w agencie CSharpier i `jb inspectcode` (hooki w `.husky/`, patrz `README.md`).

WAŻNE:
- Bez przepisywania historii i force push. Do `ambit-prd/ambit.es` pushuj dopiero po mojej zgodzie (wymóg z `CLAUDE.md` tego repo).
- Token nie może trafić do logu, komunikatu błędu, stdout/stderr programu `Ambit.Agent.Setup`, logu instalatora ani do repo.
- `POST api/optima/agent/operations/claim` przejmuje operację i zmienia jej stan. **Nie wolno** używać go do sprawdzania tokenu: operacja wykonuje się raz, a powtórzyć ją może tylko operator Ambit (`docs/adr/0003-operations-from-ambit.md`).
- Instalator nadal nie włącza zapisów (`Writes:Enabled`).
- `.instructions/` nie jest w `.gitignore` agenta. Nie commituj tego pliku przy okazji.

UWAGA, stan repo w chwili pisania (sprawdź ponownie):
- `ambit.optima.agent`: branch `feat/optima-task-queue` (5a2b89e), drzewo czyste poza tym plikiem. Instalatora nie ma na `main`, więc odgałęź się od `feat/optima-task-queue`.
- `ambit.es`: branch `feat/optima-queue` (5e19721b, moduł Optima jest tylko tutaj, nie na `main`). Niezacommitowane: `src/HrAgencySystem.Api/appsettings.Development.json` (zmieniony, lokalne dane dostępowe RustFS), `.husky/` i `runlocal.ps1` (nieśledzone). **Nie ruszaj tej pracy, nie commituj jej i nie przełączaj branchy w tym katalogu.** Potrzebujesz czystego drzewa → osobny `git worktree` od `feat/optima-queue` albo zatrzymaj się i zapytaj.

## Tryb pracy
Zanim napiszesz kod: rozpoznaj oba repo, pokaż krótki plan, zadaj pytania z listy na dole i poczekaj na decyzję. Kolejność: najpierw Temat 1 (bez tokenu nie ma czego wpisać do instalatora), potem Temat 2. Osobny branch i PR w każdym repo.

Sprawdź w dokumentacji, nie pisz z pamięci:
- Inno Setup 6 (wersja zainstalowana w `C:\Program Files (x86)\Inno Setup 6`): `TInputQueryWizardPage.Add` (pole maskowane), `{param:...}`, `ExecAndCaptureOutput`, co trafia do logu przy `/LOG`.
- .NET 10: `FileSystemAclExtensions` / `FileSecurity` (ACL pliku, wirtualne konto `NT SERVICE\...`).
- Front: `@heroui/react ^3.2.6`, `@tanstack/react-query ^5.104.5`, `@tanstack/react-start ^1.169.4`.

---

## Temat 1: Owner wystawia organizacji token dla Optimy (`ambit.es`)

Stan obecny:
- Dokument `OptimaAgentKey` (`src/HrAgencySystem.Optima/Documents/OptimaDocuments.cs`, schemat `optima`) ma już `OrganizationId` i `KeyHash` (SHA-256). Wartość `optima_` + 64 znaki hex, pokazywana raz.
- Klucz wystawia i odwołuje **Admin organizacji**, nie Owner: `src/HrAgencySystem.Optima/Application/OptimaAgentKeyHandler.cs` (`OptimaAccess.EnsureAdmin`), trasy `POST/DELETE ApiEndpoints.Optima.AgentKeys` w `src/HrAgencySystem.Api/Endpoints/Optima/Maps/MapOptimaPanel.cs`. Frontu Optimy nie ma w ogóle.
- Logowanie agenta: `src/HrAgencySystem.Api/Auth/OptimaAgentKeyAuthenticationHandler.cs`, nagłówek `X-Optima-Agent-Key`, organizacja wynika z klucza.
- Wzór po stronie Ownera (klucze programów, nieprzypisane do organizacji): `src/HrAgencySystem.Api/Endpoints/Owner/Maps/MapIssueApiKey.cs`, `MapListApiKeys.cs`, `MapRevokeApiKey.cs`, grupa `WithOwnerRole` w `Endpoints/Owner/Endpoint.cs`; front `frontend/src/platform-owner/features/api-keys/pages/components/IssueApiKeyDrawer.tsx` (jednorazowe pokazanie i kopiowanie).
- Ekran organizacji u Ownera: `frontend/src/routes/admin/organizations/$id.tsx` → `frontend/src/platform-owner/features/organizations/plan/OrganizationPlanPage.tsx` z sekcjami (`TaskRulesSection.tsx`, `OrganizationMembersSecuritySection.tsx`).
- Moduł Optima referuje tylko `SharedKernel` i z założenia nie zna innych modułów (`OptimaModule.cs`), więc istnienia organizacji nie sprawdzi sam. Rozwiąż to na poziomie API lub przez istniejący mechanizm i opisz wybór w planie.

Wymagania:
1. Owner wystawia wskazanej organizacji token dla Optimy.
2. W bazie jest zapisany token i `OrganizationId` organizacji (sposób zapisu tokenu: pytanie 1).
3. Token wystawiony przez Ownera loguje agenta danej organizacji istniejącym nagłówkiem i schematem.

Propozycje (nie wymagania, potwierdź w planie):
- Sekcja „Optima” na `OrganizationPlanPage`: lista tokenów (nazwa, kiedy wystawiony, ostatni kontakt `LastSeenAt`, `AgentVersion`, odwołany), wystawienie i odwołanie, jak w `api-keys`.
- Trasy pod `ApiEndpoints.Owners`, z organizacją w ścieżce; regeneracja klienta przez `frontend/orval.config.ts`.
- ADR: kod cytuje ADR 0073, ale pliku `docs/adr/0073-*` nie ma. Utwórz go i zapisz w nim tę decyzję. Najpierw sprawdź w `docs/adr/README.md`, czy numer jest wolny.

Poza zakresem: nazwy kontraktu z ADR 0003 (`ReadDeductionDefinitions` i `deductionDefinitions` zostają na razie po stronie Ambit), kolejka operacji, ekran operacji dla HR.

Testy:
- Handler na prawdziwym PostgreSQL (`tests/HrAgencySystem.IntegrationTests.Platform` albo projekt wskazany w `CLAUDE.md` → *Testing*): Owner wystawia → dokument ma właściwe `OrganizationId`; nieistniejąca organizacja → 404; pusta lub za długa nazwa → błąd walidacji.
- HTTP: użytkownik inny niż Owner → 403; token organizacji A na `claim` widzi tylko kolejkę A; odwołany token → 401.
- Front: test widoku sekcji; wartość tokenu widoczna tylko bezpośrednio po wystawieniu.

---

## Temat 2: Pola „Ambit Url” i token w instalatorze (`ambit.optima.agent`)

Stan obecny:
- `installer/AmbitOptimaAgent.iss`: strony `SqlPage` i `FirmsPage` tworzone w `InitializeWizard`, walidacja w `NextButtonClick` (przy `WizardSilent` nie działa), `Apply` woła `Ambit.Agent.Setup.exe apply` z argumentami w linii poleceń i loguje jego stdout.
- `src/Ambit.Agent.Setup/SetupArguments.cs`: parser opcji. `src/Ambit.Agent.Setup/WorkerConfiguration.cs`: `Merge` i `Write` piszą `{app}\appsettings.Production.json`, aktualizacja zachowuje ustawienia administratora.
- `src/Ambit.Agent.Setup/Program.cs`: `WorkerConfiguration.Write` działa **przed** `ServiceRegistration.RegisterAsync`. Konto `NT SERVICE\AmbitOptimaAgent` nie istnieje przed utworzeniem usługi, więc ACL dla niego nie da się ustawić wcześniej.
- `src/Ambit.Agent.Infrastructure/AmbitApi/AmbitApiOptions.cs`: `Enabled`, `BaseUrl`, `Key`; walidator wymaga https (http tylko dla loopback) i klucza, a usługa sprawdza to przy starcie (`ValidateOnStart`), więc zły adres zatrzyma usługę. Komentarz klasy każe trzymać klucz w `AmbitApi__Key`.
- `docs/adr/0004-client-installer.md` mówi, że instalator nie włącza połączenia, dopóki nie zapadnie decyzja, gdzie trzymać klucz. To zadanie tę decyzję zamyka.
- `{app}` leży w Program Files, czytelnym dla lokalnych użytkowników. Odinstalowanie usuwa `appsettings.Production.json`.
- `HttpAmbitApiClient` dokleja do adresu `api/optima/agent/...`. Domyślny `app.ambitsys.eu` to panel: żądanie przejdzie przez proxy `frontend/src/routes/api/$.ts` w `ambit.es`, a API ma własny host `api.ambitsys.eu` (`deploy/README.md`). Proxy przekazuje nagłówki, ale przy 401 próbuje odświeżyć sesję, a przed nim stoi Cloudflare. Tej drogi agent jeszcze nie przeszedł.

Wymagania:
1. Kreator ma pole **Ambit Url**, domyślnie `https://app.ambitsys.eu`.
2. Kreator ma pole na **token** wystawiony przez Ownera (Temat 1).
3. Wartości trafiają do ustawień Workera (`AmbitApi:BaseUrl`, `AmbitApi:Key`) i agent łączy się z tym adresem tym tokenem.

Propozycje (nie wymagania, potwierdź w planie):
- Nowa strona kreatora po `FirmsPage`, pole tokenu maskowane, walidacja adresu tą samą regułą co `AmbitApiOptionsValidator` i formatu tokenu (`optima_` + 64 hex). `Ambit.Agent.Setup` waliduje też sam, bo tryb cichy pomija `NextButtonClick`.
- Aktualizacja: pole adresu pokazuje wartość z istniejącego pliku, a nie domyślną; puste pole tokenu zachowuje obecny klucz.
- Token nie idzie linią poleceń, bo tę widzą lokalni użytkownicy: `--ambit-token-file` wskazuje plik w `{tmp}`, usuwany zaraz po wywołaniu. Tryb cichy (ADR 0004): `/AMBITURL=...` i `/TOKENFILE=...`.
- Przechowywanie: `AmbitApi:Key` w `appsettings.Production.json` z ACL bez dziedziczenia, odczyt tylko dla `SYSTEM`, `Administrators` i `NT SERVICE\AmbitOptimaAgent`. ACL ustawiane po `RegisterAsync`. Uwaga: plik do tej chwili nie może zawierać tokenu przy dziedziczonych uprawnieniach; dopilnuj kolejności.
- Decyzję zapisz w nowym `docs/adr/0005-*` (ostatni to 0004; sprawdź, czy nie ma nowszych), popraw ADR 0004, komentarz w `AmbitApiOptions` i `README.md` (status oraz adres).

Poza zakresem: `Writes:Enabled`, schemat `agent-ambit` i skrypty `db/agent-ambit`, UI Flutter, zmiana sposobu wyboru firm, podpisywanie instalatora.

Testy:
- `tests/Ambit.Agent.UnitTests/SetupTests.cs`: nowa konfiguracja dostaje adres i token; aktualizacja z pustym tokenem zachowuje klucz i `PollSeconds`; nowy token zastępuje stary; adres `http://` poza loopback i token w złym formacie są odrzucane; błąd nie zawiera wartości tokenu; parser `apply` z nowymi opcjami. Istniejący test `An_upgrade_keeps_what_an_administrator_set` ma nadal przechodzić.
- Ręcznie na tej maszynie, z `./installer/build.ps1`: pierwsza instalacja i aktualizacja, kreatorem i po cichu (`/LOG`). Sprawdź: `icacls` na pliku ustawień, zwykły użytkownik nie czyta pliku, usługa czyta; szukanie tokenu w logu instalatora i w tabelach logów `agent-ambit` nic nie znajduje; zły adres daje czytelny błąd zamiast zawieszenia na `VerifyAsync`.
- End to end z lokalnym Ambitem z Tematu 1: agent z tokenem dostaje `204` z `claim`, u Ownera rosną `LastSeenAt` i `AgentVersion`; po odwołaniu tokenu agent dostaje 401, działa dalej i nie wykonuje operacji.
- Droga przez `https://app.ambitsys.eu` (proxy i Cloudflare): potwierdź, że `X-Optima-Agent-Key` dociera, a 401 nie wpada w odświeżanie sesji. Jeśli nie działa, zatrzymaj się i pokaż mi to; nie zmieniaj domyślnego adresu samodzielnie. Sprawdzenie na produkcji wymaga mojej zgody i wdrożenia Tematu 1.

---

## Pytania do mnie, zanim zaczniesz
1. **„Trzyma w bazie token”: wartość czy skrót?** Dziś jest tylko SHA-256 i token pokazuje się raz. Propozycja: zostawić skrót, a przy zgubieniu wystawić nowy. Jeśli Owner ma móc odczytać token później, trzeba go zapisać odwracalnie (szyfrowanie jak sekrety KSeF), co zwiększa skutki wycieku bazy.
2. **Co z wystawianiem przez Admina organizacji?** Trasy już są, frontu nie ma. Propozycja: usunąć `POST` i `DELETE`, żeby tokeny wystawiał tylko Owner. Odczyt listy dla organizacji do decyzji.
3. **Ile aktywnych tokenów na organizację?** Dziś dowolnie wiele. Jeden agent to jedna organizacja, ale wymiana bez przestoju wymaga dwóch naraz. Propozycja: wiele, odwoływane ręcznie.
4. **Czy token jest obowiązkowy?** Propozycja: tak przy pierwszej instalacji wykonanej kreatorem, a podanie tokenu ustawia `AmbitApi:Enabled = true`. Alternatywa: puste pole instaluje agenta bez połączenia, jak dziś.
5. **Czy instalator ma sprawdzać token w Ambit?** `claim` się do tego nie nadaje (patrz WAŻNE), więc potrzebna byłaby nowa trasa tylko do odczytu po stronie agenta. Propozycja: dodać ją i przy niepowodzeniu ostrzegać bez blokowania, bo klient może jeszcze nie mieć otwartego wyjścia na świat.
6. **Czy przechowywanie z ACL wystarczy** (propozycja w Temacie 2)? Alternatywa: dodatkowe szyfrowanie DPAPI. Bez ACL nie chroni przed lokalnym programem, a utrudnia diagnostykę.

Na koniec: raport (co zrobione, decyzje i założenia, co odłożone, wynik testów ręcznych), PR w każdym repo z opisem decyzji. W `ambit.es` otwórz PR dopiero po mojej zgodzie na push.
