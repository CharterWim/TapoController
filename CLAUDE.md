# Tuinverlichting – C# lichtshow-controller voor Tapo-lampen

Communiceer in het Nederlands. Ontwikkelaar: Wim (C#/.NET, embedded; Windows + Visual Studio Community).

## Doel
Een C#-applicatie die 15 Tapo-lampen in de tuin lokaal aanstuurt met rustige, organische animaties
(golven in blauwtinten, smeulend vuur in rood/oranje, scènes), zonder dat het onrustig wordt voor bewoners of buren.
Later: draaien op een Raspberry Pi 3 (Raspberry Pi OS Lite 64-bit, self-contained publish linux-arm64,
systemd-service), met een kleine webportal (ASP.NET Core + Blazor Server) om scènes te maken/kiezen,
een REST-API (bv. POST /api/scenes/{naam}/start, /api/stop) en Siri-integratie via iOS Opdrachten
of Homebridge (scènes als HomeKit-schakelaars).

## Hardware
- 13x Tapo L630 (GU10, kleur) op de schutting: Tuin_L1 t/m Tuin_L13.
- 2x Tapo L535 (E27, kleur) in een lantaarnpaal: Lantern_1, Lantern_2.
- Tapo H200 hub + S200B knop (lokaal geen betrouwbaar knopdruk-event; workaround via Tapo Smart Action
  die een lampstatus verandert, die de app pollt).
- Alle lampen hebben de nieuwste firmware.

## Opstelling (vanaf het terras gezien; de U opent naar het terras)
- Tuin_L1–L6: noordzijde = rechterschutting, van het terras naar achteren. Grenst aan buurhuis met ramen → lager helderheidsplafond.
- Tuin_L7–L9: oostzijde = achterschutting rechts van het hek.
- Onderbreking: hek + lantaarnpaal (Lantern_1/2) tussen L9 en L10.
- Tuin_L10–L13: zuidzijde, van achter-links naar de linkerschutting, eindigend linksvoor.
- Elke lamp schijnt een lichtvlek op de schutting; gras/bodem kleuren sterk mee → lage helderheid en verzadiging onder 100% voor sfeer.
- Lantaarn kleurt zijn stuk schutting mee; standaard warm wit, doet optioneel mee per scène, met crossfade van/naar zijn basisstand.

## Ontwerpbesluiten
- Lampen hebben posities in meters (gat tussen L9 en L10 is een echte afstand). Effect = f(positie, tijd) → HSV.
- Richting per effect: lineair (L1→L13 of omgekeerd) of gespiegeld vanuit de lantaarn (midden achterin).
- Update-rate ~2 Hz per lamp; per lamp één hergebruikte sessie, geen parallelle requests naar dezelfde lamp;
  stap overslaan als de lamp achterloopt i.p.v. opstapelen. Alleen sturen bij merkbare verandering.
- Hue-interpolatie via de kortste weg rond 360°. color_temp = 0 bij hue/saturation.
- Golven: twee trage sinussen (periode 30–60 s), hue ~190–235°, helderheid ~35–60%.
- Vuur: trage ruis (Perlin/value noise) op hue ~8–35° en helderheid, deels gedeeld tussen buren; geen snel flikkeren.
- Max. verandering per stap, helderheidsplafond per zone, avondcurve (bv. dimmen na 22:00), crossfade van minuten bij effectwissel.
- Configuratie in JSON (zones, lampen, posities, basisstand lantaarn). Credentials nooit in code: user secrets / config buiten de repo.
- Lampen hebben ingebouwde dynamische effecten (app: "Verlichtingseffecten", incl. eigen effecten). Te onderzoeken
  welk commando/formaat ze gebruiken (diff van get_device_info voor/na activeren in de app).

## Protocolkennis
- Gemeten (discovery 2026-11-10): ALLE lampen (13x L630, 2x L535E) en de P110 gebruiken **TPAP**,
  `tpap: {tls:0, dac:0, noc:0, pake:[2], port:80}` → plain HTTP poort 80, inloggen met accountwachtwoord.
  KLAP/securePassthrough zijn dus niet nodig. H200 hub: alleen https.
- TPAP (overgezet van Rust-crate `tapo`, mihai-dinculescu/tapo, tpap_*.rs):
  - POST http://ip/ `{"method":"login","params":{"sub_method":"pake_register", username=md5hex("admin"),
    user_random (32B b64), cipher_suites:[1], encryption:["aes_128_ccm"], passcode_type:"userpw"}}`
    → dev_random, dev_salt, dev_share, iterations, extra_crypt.
  - extra_crypt password_shadow passwd_id 2 → credential = sha1hex(wachtwoord); 4 → wachtwoord zelf.
  - SPAKE2+ (RFC 9383, P-256): PBKDF2-SHA256(credential, salt, iter, 80B) → w0/w1 (elk 40B mod n);
    context = SHA256("PAKE V1"+user_random+dev_random); lege identities.
  - pake_share (user_share, user_confirm) → dev_confirm, stok, start_seq.
  - Data: POST /stok=<stok>/ds, body = seq(4B BE) + AES-128-CCM(tag 16) ; key/iv via HKDF uit shared key.
  - Error -2101 = te veel mislukte logins (lamp geblokkeerd) → bij credentialfout direct stoppen.
- Credentials: Tapo-accountwachtwoord (user secret `Tapo:Password`); gebruikersnaam is altijd "admin".
- Commando's: get_device_info, component_nego, set_device_info (device_on, brightness 1–100, hue 0–360,
  saturation 0–100, color_temp), multipleRequest, effect-gerelateerde methodes per component_nego.
- python-kasa noemt L630 als ondersteund; L535 niet. Referenties: python-kasa, Rust-crate `tapo`.
- Discovery: UDP 20002, 16-byte header + JSON met RSA public key; antwoord bevat mgt_encrypt_schm.encrypt_type.

## Status
- Bouwen: 64-bit `C:\Program Files\dotnet\dotnet.exe` gebruiken; de x86-dotnet staat eerder in PATH en heeft geen SDK's.
- Stap 1 (klaar): `TapoProbe discover` → discovery.json; alle 15 lampen gevonden.
- Stap 2 (klaar, 15/15 lampen): `TapoProbe login [ip...]` → TPAP-login,
  component_nego + get_device_info per lamp → devices/<ip>.json. Crypto-check: `TapoProbe selftest`.
  - Valkuil: de lampserver ("SHIP 2.0") snapt geen chunked request bodies → altijd Content-Length sturen
    (dus geen PostAsJsonAsync); anders komt er een HTML-pagina "200 OK" terug.
  - Login duurt ~1,9 s bij L630 (fw 1.4.2), ~0,6 s bij L535 (fw 1.4.5) → sessie hergebruiken, niet per request inloggen.
  - IP's: L01 .34, L02 .232, L03 .84, L04 .38, L05 .51, L06 .171, L07 .141, L08 .77, L09 .166, L10 .116,
    L11 .238, L12 .216, L13 .103, Lantern_01 .90, Lantern_02 .247 (192.168.1.x; nog DHCP → reserveren).
  - Effecten: L630 heeft component `light_effect` (velden dynamic_light_effect_enable/_id);
    L535 heeft `segment_effect` (veld segment_effect). Formaat van eigen effecten nog uitzoeken.
- Aansturen getest (`TapoProbe control <ip>`, Tuin_L01): set_device_info {hue,saturation,brightness,color_temp:0}
  werkt; mediaan 129 ms per request, uitschieter 692 ms (20 stappen op 2 Hz) → 2 Hz per lamp haalbaar,
  wel stap overslaan bij achterlopen. Oorspronkelijke stand wordt na de test teruggezet.
  - Verste lampen: L09 rssi -63 dBm, mediaan 111 ms / max 398 ms; L13 rssi -78 dBm (signal_level 1),
    mediaan 150 ms / max 971 ms, login ~3 s. L13 is de zwakke plek qua wifi.
  - **Testlamp voor kijktests: Tuin_L09 (192.168.1.166)** — die ziet Wim het best.
- Vloeiendheid: bij 2 Hz met grote stappen (golf van 10 s) zijn stapjes zichtbaar.
  - set_device_info negeert onbekende parameters stil (error_code 0), dus acceptatie zegt niets over werking.
  - **L630 heeft een vaste ingebouwde fade van ~1–1,5 s bij elke kleurwissel; niet instelbaar.**
    - `transition` in set_device_info doet niets (1000/5000/5/10000 identiek).
    - change_state.duration wordt geaccepteerd maar niet opgeslagen.
    - on_state.duration (1–60 s) wordt wel opgeslagen, maar beïnvloedt kleurwissels NIET. Gemeten met
      `TapoProbe fadetest` (Enter bij start/stil, incl. reactietijd) bij duration=5: 1,8 / 1,5 / 1,2 / 1,5 s
      voor blauw→paars en rood→blauw, sat/bri 80/40 en 100/50. Eerdere "~5 s"-indrukken waren schattingen
      met de klok, onbetrouwbaar → voor kijktests voortaan fadetest-achtige meting gebruiken.
    → Vloeiendheid moet uit eigen stapjes komen, met een tempo rond die ingebouwde fade.
  - Golf op echt tempo (periode 40 s, update elke 0,7 s, 60 s, L09): "al heel vloeiend, af en toe een stapje".
    86 updates, 0 overgeslagen, mediaan 112 ms, max 305 ms.
    Tools: `TapoProbe wave <ip> [periode s] [interval ms] [transition ms] [duur s]`, `TapoProbe fade <ip> <h> <s> <b> [transition]`.
    Nog te checken: gedrag van de L535.
  - Kijktests L09 (golf periode 20 s): update elke 1 s → pulserend; elke 3 s → 1 s fade + 2 s stilstand.
  - wavemark (periode 40 s, 0,7 s, 120 s, L09): "eigenlijk al heel mooi, minimale stapjes". 4 markeringen,
    172 updates, 0 overgeslagen, latency max 353 ms, intervallen 690–715 ms → NIET timing-gerelateerd.
    3 van 4 markeringen op exact dezelfde golffase (5,8 / 45,7 / 85,3 s = 40 s uit elkaar): hue ~230 stijgend,
    helderheid op de top (60) en net omkerend. Kandidaten: (a) 1%-helderheidsstap na stilstand op de top,
    (b) kleurmenging bij diepblauw (groenkanaal bijna 0 → lage-PWM-kwantisatie).
    Vervolg (elk 120 s, L09): alleen-helderheid (35–60%, hue 212) → 0 markeringen, ook niet bij requests
    van 400–690 ms. Alleen-tint (190–235, bri 48) → 1 markering, op de top hue 235 net omkerend.
    → Helderheid en timing zijn het niet. Gemene deler van 4 van 5 markeringen: hue 229–235 (diepblauw),
    erger in combinatie met hoge helderheid. Bevestigingstest (hue-bereik kappen op ~225) nog te doen.
    Tool: `TapoProbe wavemark <ip> [periode] [interval] [duur]` → bin/Debug/net10.0/wavemark-*.csv.
  - get_on_off_gradually_info: change_state.enable=true, on/off duration 1 s (max 60).
  - `TapoProbe raw <ip> <method> [json] [wait ms] ...` voor experimenten.
  - Kijktests: Wim draait interactieve tests zelf in een terminaltab van de app (run_in_terminal);
    uitleg altijd vóór de start tonen, niet tijdens de test. fadetest schrijft naar bin/Debug/net10.0/fadetest.log.
- Stap 3 (klaar): library **Tuinverlichting.Tapo** (solution `Tuinverlichting.slnx`):
  - `TapoLamp`: blijvende sessie per lamp, één request tegelijk (SemaphoreSlim), automatisch opnieuw inloggen
    bij TapoException.IsSessionInvalid / HTTP-fout / timeout (één keer per request), `Submit(Hsv)` = nieuwste wint
    (geen opstapeling, gelijke afgeronde kleur wordt niet verstuurd), `Stats` voor diagnose, `Error`-event.
  - `Hsv` (doubles, afronding/clamp bij versturen, `Lerp` via kortste hue-weg), `TapoException` (public).
  - TPAP-internals (Spake2Plus, TpapCipher, TpapClient) zijn internal; InternalsVisibleTo voor tests en TapoProbe.
  - Tests: `Tuinverlichting.Tapo.Tests` (xunit, 14 tests: crypto-vectoren, credential, cipher, Hsv).
  - **lamps.json** (repo-root): naam, ip, zone, pos, maxBrightness. pos voorlopig in lampplaatsen
    (L01–L09 = 0–8, gat, L10–L13 = 11–14, lantaarns 9,5) → nog vervangen door echte meters van Wim.
    Noordzijde L01–L06 maxBrightness 50 (buren), rest 70.
  - TapoProbe gebruikt de library; user secrets via de TapoProbe-assembly (library heeft geen UserSecretsId).
  - `TapoProbe connectall`: 15/15 parallel verbonden in 2,8 s, alle nicknames = namen in lamps.json.
  - `TapoProbe fencewave [periode s=30] [duur s=90]`: golf over 13 schuttinglampen (hue 190–228, sat 70,
    bri 35–60 met zoneplafond, golflengte 10 plaatsen, update 0,7 s); zet alles terug. Droge run 5 s:
    0 fouten, 0 overgeslagen, mediaan ~135 ms, alle lampen correct teruggezet.
  - Eerste echte fencewave (90 s, 2026-11-10): **Tuin_L01 viel na ~25 s volledig van het netwerk** (geen ping,
    niet in UDP-discovery, MAC C0-3A-55-56-34-63) → herhaalde 10 s-timeouts, niet teruggezet.
    Overige lampen: mediaan 115–165 ms, maar uitschieters 2–7,5 s (L13 15 s incl. relogin), 2–20 updates
    overgeslagen per lamp. In de 5 s-droogrun max ~230 ms → belasting/wifi bij ~13×1,4 req/s over langere tijd?
    Bug: eindmelding "alle lampen teruggezet" ook als terugzetten mislukte (opgelost).
    L01 bleef daarna branden in golfkleur maar onbereikbaar → stroom eraf/erop nodig.
  - Aanpassingen daarna: TapoLamp.RequestTimeout 5 s (was 10), onbereikbare lamp in Submit() 20 s overslaan
    (OfflineBackoff, Stats.OfflinePeriods/SkippedOffline); fencewave stuurt gespreid (per tik één lamp, tik =
    interval/aantal) i.p.v. 13 tegelijk; args `fencewave [periode=10] [duur=90] [interval=700] [tint min=190]
    [tint max=240]` — Wim wil diepblauw erbij en "voor nu" een veel sneller tempo. Eindmelding noemt
    niet-verbonden en niet-teruggezette lampen.
  - fencewave 10 s / 700 ms / tint 190–240, alle 13 (L01 na stroomreset terug): 0 fouten, 0 overgeslagen,
    latency mediaan ~120 ms, max <1 s → gespreid sturen werkt. Maar Wim: "erg grote stappen" (bij periode 10 s
    is dat tot ~11° hue per update). Volgende test: interval 350 ms (~37 req/s totaal).
  - fencewave 10 s / 350 ms: ~250 updates per lamp in 90 s, 0 fouten, max ~0,8 s → wifi kan ~37 req/s aan.
    Maar nog steeds "te grof". Conclusie: de per-lamp veranderingssnelheid (amplitude × 2π / periode) bepaalt
    de zichtbare stappen, niet het interval. Ontwerpregel: loopsnelheid langs de schutting = golflengte/periode;
    voor snel lopende patronen golflengte én periode groot maken (lang kleurverloop dat schuift).
    Connect: tot 3 pogingen (13 parallelle logins halen soms de 5 s-time-out niet).
    `fencewave 30 90 350 190 240 30` (golflengte = 7e argument) → **"vloeiend"** (2026-11-11), alle 13 lampen,
    0 fouten, ~15% van de updates "ongewijzigd" (niet verstuurd), max latency <0,9 s. Diepblauw (tot 240)
    stoort bij dit tempo niet. → Ontwerpregel bevestigd: per-lamp hue-snelheid ≲ ~5°/s houden
    (hier 2π·25/30 ≈ 5,2°/s); snelle beweging maken via lange golflengte, niet via korte periode.
- Git: repo op `main`, remote origin = https://github.com/CharterWim/TapoController (waarschijnlijk openbaar).
  Systeem-git is 2.7 (oud, geen browserlogin): pushen met fine-grained token in de pop-up, of via Visual Studio.
  .gitignore houdt devices/ en discovery.json (GPS, SSID, MAC's, device-ID's) en logs buiten git.
- Stap 4 (klaar; scenedemo 60/20 s visueel beoordeeld door Wim: "mooi", 2026-11-11, gedimd ×0,6 na 22:00;
  13/13 lampen, 0 fouten, ~275 updates per lamp, max latency ~1,1 s): **Tuinverlichting.Scenes**
  - `Effect` (abstract record, JSON-polymorf via "type": golf/vuur/vast): `WaveEffect` (HueMin/Max, Saturation,
    BrightnessMin/Max, PeriodS, Wavelength, Direction Forward/Backward/FromLantern/ToLantern), `FireEffect`
    (value noise 2D, 2 octaven, TimeScaleS 8, SpatialScale 0,35 → buren gecorreleerd, Seed), `StaticEffect`.
  - `SceneEngine`: per lamp per stap effect op t+Lookahead (1 s) → crossfade (smoothstep) → avondcurve
    (22:00–23:00 naar ×0,6, tot 06:00 gedimd) → zoneplafond → snelheidsbegrenzer (6°/s tint, 10%/s sat,
    4%/s helderheid t.o.v. vorige uitvoer). Eerste effect fadet vanuit de huidige lampkleur (SetInitialState).
    `RunAsync` stuurt gespreid (Interval 700 ms / aantal lampen). ComputeStep/ComputeFrame zijn puur → testbaar.
  - `GardenConfig`/`LampConfig` (lamps.json) verhuisd naar Scenes; `ILightOutput` (in Tapo) wordt door TapoLamp
    geïmplementeerd zodat de engine met nep-uitgangen te testen is.
  - Tests: 30 totaal (16 nieuw: effecten, crossfade, plafond, avondcurve, begrenzer incl. 360°-wrap, JSON).
  - `TapoProbe scenedemo [deel s=60] [crossfade s=20]`: golf vanuit lantaarn → vuur → golf L01→L13.
    Droge run 3×4 s: 13/13, 0 fouten, alles teruggezet.
  - L01 kwam na de stroomreset terug op standaardstand (bri 100, hue 302, sat 14); handmatig op 338/19/33 gezet.
  - Vuur iteratie 2 ("meer helderheidsverschil, meer rood↔oranje"): bereik 0–32° / 15–65%, contrast 1,8,
    begrenzer helderheid 4→8 %/s. Wim: "erg schokkerig en lijkt in een wave te bewegen".
    Oorzaken: 2D-ruis over (positie, tijd) → patronen schuiven langs de schutting; te snel helderheidslaagje
    (begrenzer kapt af = knikjes); bij ~9% (met avondcurve) is 1% stap zichtbaar.
  - Vuur iteratie 3: per lamp eigen 1D-ruis (rij id = pos*10) + 25% gedeelde trage "adem"; tint 7 s, helderheid
    5 s per ruispunt, contrast 1,5, helderheid 25–65%. Tests: buren r<0,5 bij elke vertraging 0,7–7 s (geen golf),
    p95 van de snelheid binnen de begrenzer (6°/s, 8%/s). Wim: "sneller, meer willekeurig, lampen lijken allemaal
    hetzelfde te doen, nog schokkerig, mag wilder".
  - Vuur iteratie 4 (andere aanpak): elke lamp springt om de 0,7–1,4 × FlickerS (1,2 s, eigen periode en fase per
    lamp) naar een nieuwe loting (helderheid 70% / tint 50% geloot, rest trage onderstroom per lamp); de ingebouwde
    ~1 s-fade van de L630 maakt er flakkeringen van (het "optrekken/afremmen" dat bij golven stoorde is hier gewenst).
    `Effect.UsesLampFade` → engine slaat de snelheidsbegrenzer over na de crossfade. Bereik 0–35° / 20–75%.
    Wim: "veel beter, maar kleurverschillen mogen groter; laagste helderheid mag naar 5%". 13/13, 0 fouten.
  - Vuur iteratie 5: tint 0–45°, HueRandomness 0,7, helderheid 5–75%. Wim: "lampen veranderen tegelijk in een
    patroon van ~2 s". Oorzaak: engine bemonsterde elke lamp maar elke 0,7 s → alle wissels op een gemeenschappelijk
    raster (aliasing met flakkerperiodes ~1,2 s).
  - Vuur iteratie 6: vuur is stapsgewijs constant (ook de onderstroom per flakkering bemonsterd), tijdvervorming
    (0,8·Value2D(t/4), helling ≤0,3) → elke flakkering eigen duur; `Effect.PreferredInterval` (vuur 200 ms) en
    `SceneEngine.IntervalAt(t)`: na de crossfade bemonstert RunAsync het vuur elke 200 ms per lamp; verkeer blijft
    ~1 opdracht per flakkering omdat gelijke waarden niet verstuurd worden. 34 tests.
    **Wim: "mooi"** (2026-11-11) → dit is de huidige vuurversie.
  - Scène **Lantaarnnacht** (`LanternNightEffect`, JSON "lantaarnnacht", eigen ontwerp op verzoek van Wim):
    schutting traag blauw-violet maanlicht (hue 220–262, sat 65, bri 18–38, periode 45 s, golflengte 26, vanuit de
    lantaarn); lantaarns amber kaarsgloed (hue 30±6, sat 85, bri 50–75, ademt per lantaarn eigen ritme, BreathS 7);
    gloed op de schutting rond de paal (HaloStrength 0,75, radius 2,2 plaatsen, gaussisch) via kortste hue-weg
    blauw→violet→roze→amber. LampPoint kreeg IsLantern (zone "lantaarn") en Id (hash op naam, want beide
    lantaarns staan op pos 9,5). Lantaarns doen alleen mee in scènes die ze een rol geven (scenedemo zonder).
    Eerste keer L535 in kleur aangestuurd: werkt (hue/sat/brightness, color_temp 0); terugzetten naar
    color_temp 6310 werkt (lamp meldt daarna nog de oude hue/sat, maar staat in witmodus). 38 tests.
    Droge run 8 s: 15/15, 0 fouten. **Wim: "mooi"** (2026-11-11).
  - Scène **Looplicht** (`ScannerEffect`, JSON "looplicht"): rode kop heen en weer (SweepS 5 s per doorgang,
    cosinus-afremming bij de uiteinden), Width 1,5 plaats; lamp binair aan (100%) / uit (1%) → ~2 opdrachten per
    lamp per doorgang, de ingebouwde ~1 s fade maakt oplichten + staart. `Effect.UsesLookahead = false` zodat de
    staart áchter de kop valt (engine evalueert dan op t i.p.v. t+1 s). Gat L09–L10 dichtgeschoven (GapStart 8,
    GapEnd 11). PreferredInterval 100 ms. `TapoProbe scene looplicht [duur] [s per doorgang]`. 42 tests.
    Droge run 10 s: 13/13, 0 fouten. **Wim: "mooi"** (2026-11-11, 5 s per doorgang).
  - `TapoProbe scene <golf-lantaarn|vuur|golf> [duur s=120]`: één scène los bekijken.
  - Git: credential.helper = wincred (globaal) → token na volgende push bewaard in Windows Referentiebeheer.
- Stap 5 (klaar, lokaal getest): **Tuinverlichting.Host** (ASP.NET Core minimal API, poort 5080, 0.0.0.0)
  - `GET /api/status`, `GET /api/scenes`, `POST /api/scenes/{key}/start?fade=20`, `POST /api/stop?fade=10`,
    `GET /health`. Optioneel `Api:Key` → header `X-Api-Key` vereist (constant-time vergelijking).
  - `LightController`: alle 15 lampen bij opstart verbinden (3 pogingen) en basisstand onthouden (kleur of
    kleurtemperatuur); start vanuit rust = engine.Reset(basis) + crossfade; wisselen = crossfade vanaf vorige
    scène; lantaarns alleen aangestuurd in scènes met UsesLanterns, bij wissel naar andere scène direct terug
    naar hun witte basisstand; stop = crossfade naar `PerLampStaticEffect(basis)`, daarna exact terugzetten
    (incl. color_temp); dienst stopt → direct terugzetten. Commando's geserialiseerd (SemaphoreSlim).
  - `SceneCatalog` (Scenes-library) = gedeelde scènelijst voor host en TapoProbe; `SceneEngine` kreeg
    Reset, SetActive/IsActive (nieuw geactiveerde lamp fadet vanaf eigen startkleur), RunAsync slaat inactieve
    lampen over.
  - Wachtwoord: zelfde UserSecretsId "tuinverlichting-tapo" (ook buiten Development geladen); Pi: Tapo__Password.
  - Lokale test (localhost:5080): 15/15 verbonden, lantaarnnacht → vuur (lantaarns terug naar wit) → stop;
    alle lampen exact op basisstand. 46 tests.
  - Let op: basisstand = stand bij het opstarten van de host. Start je de host terwijl er nog een scène-kleur
    op de lampen staat, dan wordt dát de basis.
- Inschakelgedrag (flash wear): lampen stonden op `default_states.type = "last_states"` (laatste stand
  onthouden → mogelijk flash-schrijfactie per wijziging). L09 omgezet naar
  `set_device_info {"default_states":{"type":"custom","re_power_type":"always_on","state":{338/19/33}}}` →
  opgeslagen, en de opgeslagen stand verandert niet meer mee met kleurwissels (Wim: "het is goed").
  `re_power_type: "always_off"` wordt geaccepteerd maar NIET opgeslagen; bekende waarden zijn alleen
  `always_on` en `last_states` → "uit na stroomonderbreking" kan niet (bevestigd in de Tapo-app: alleen
  "Inschakelen" of "In-/uitschakelen"). App-mapping: "Laatste keer 'Aan'" = type last_states,
  "Aangepaste verlichting" = type custom, "Inschakelen" = re_power_type always_on.
  **2026-11-11: alle 15 lampen op custom + always_on** (schutting 338/19/33, lantaarns color_temp 6310 / 100%),
  per lamp teruggelezen. L09 in de app gecontroleerd: "Aangepaste verlichting #FFCFE0 | 33%".
- Daarna: Siri (iOS Opdrachten), scènes als JSON, portal, deploy naar Pi 3.
  Open: echte posities in meters, DHCP-reserveringen.
