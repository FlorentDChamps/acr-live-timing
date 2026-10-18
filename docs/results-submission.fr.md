# Envoi des résultats

ACR Live Timing peut envoyer les résultats qu'il a observés à un **serveur tiers**,
par exemple celui d'une ligue ou d'un championnat. Ce document est le contrat vu du
client. Tout serveur qui l'implémente peut recevoir des résultats ; ACR Live Timing
n'embarque aucun serveur, aucune adresse de serveur, et ne fait aucune requête tant
que l'opérateur n'a pas collé de clé.

Ce document décrit la version 1 du protocole : les versions de schéma ci-dessous
(`acrlt1:`, `acr-pairing-key/1.0`, `acr-pairing/1.0`, `acr-pairing-response/1.0`,
`acrlt-results/1.0`) la portent. Les mots-clés d'exigence (DOIT, DEVRAIT, PEUT)
ont leur sens habituel.

*Version française de [results-submission.md](results-submission.md), qui
fait foi. Les libellés de l'interface sont en anglais, comme dans l'application.*

## Principes

- **Générique.** Le client ne connaît aucun serveur et n'applique les règles
  d'aucun serveur. Un serveur ne renvoie que son épreuve (`eventId`, `eventName`),
  un nom d'affichage facultatif (`serverName`) et des réglages d'épreuve
  facultatifs, que l'opérateur peut modifier.
- **Faits bruts.** Le client envoie, pour chaque spéciale, le statut, le temps, la
  pénalité, les secteurs et la voiture de chaque pilote. Ni position, ni
  total, ni points, ni disqualification : le serveur valide et classe.
- **Manuel.** Rien ne part tant que l'opérateur n'a pas cliqué sur *Send* après un
  aperçu. Pas d'envoi automatique ni de nouvel essai automatique : si un envoi
  échoue, l'opérateur renvoie.
- **Instantané complet.** Chaque envoi contient toute la sélection. Une correction
  est simplement un nouvel envoi ; le serveur garde le dernier.

```
serveur                                hôte (ACR Live Timing)
───────                                ──────────────────────
remet une clé d'association ────────►  colle la clé, vérifie l'hôte, clique Pair
point d'échange           ◄──────────  POST /v1/ingest/pairings/exchange
                          ──────────►  jeton + URL d'envoi + épreuve
                                       … rallye …
point d'envoi             ◄──────────  Send : POST {submissionUrl} (instantané complet)
                          ──────────►  202 stored, association: active|ended
                                       réponse affichée sur l'association
```

## 1. Clé d'association

```text
acrlt1:<base64url, sans remplissage, d'un document JSON UTF-8>
```

```json
{
  "schemaVersion": "acr-pairing-key/1.0",
  "serverUrl": "https://results.example.org",
  "pairingCode": "7K9M-4QPX"
}
```

| Champ | Règle |
|---|---|
| `schemaVersion` | `acr-pairing-key/1.<mineure>`. |
| `serverUrl` | URL `https` absolue. Un chemin, une adresse IP et un port sont autorisés (`https://203.0.113.10:8443/acr`) ; identifiants (`user:pass@`), requête, fragment et `/` final ne le sont pas. Le certificat TLS doit être valide pour l'hôte. |
| `pairingCode` | ASCII imprimable, 1 à 128 caractères. Son format et sa durée de vie regardent le serveur. |

- Les champs inconnus sont ignorés. La clé entière est limitée à 4 Kio. Une clé
  invalide est refusée avec la raison (préfixe, base64url, JSON, version ou URL).
- **Les builds Debug seulement** acceptent aussi `http` en clair vers un serveur de
  test sur le même PC ou le réseau local : `localhost`, ou une adresse IP privée
  écrite en chiffres (`127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`,
  `192.168.0.0/16`, `::1`, `fc00::/7`), avec n'importe quel port, par exemple
  `http://192.168.1.20:8080`. Jamais un nom d'hôte autre que `localhost`. Les
  builds Release n'acceptent que `https` et valident toujours les certificats.
- La clé n'est ni chiffrée ni signée, et contient le code : c'est un secret
  temporaire.
- **L'hôte est affiché avant toute requête** : l'hôte de `serverUrl` en punycode
  (`IdnHost`), avec le port s'il n'est pas 443. L'opérateur le vérifie avant de
  cliquer sur *Pair* ; c'est la seule ancre de confiance.

## 2. Échange

```http
POST {serverUrl}/v1/ingest/pairings/exchange
Content-Type: application/json; charset=utf-8

{
  "schemaVersion": "acr-pairing/1.0",
  "pairingCode": "7K9M-4QPX",
  "clientInstanceId": "0199e3b2-6d1f-7c2a-9e41-5b8f0c3d2a17",
  "appVersion": "1.4.0"
}
```

`clientInstanceId` est un UUID aléatoire généré une fois par installation, pour le
support et l'audit ; il ne prouve rien.

Un succès est un `2xx` avec :

```json
{
  "schemaVersion": "acr-pairing-response/1.0",
  "submissionToken": "<opaque>",
  "submissionUrl": "https://results.example.org/v1/ingest/rally-results",
  "eventId": "summer-2026-r03",
  "eventName": "Summer Rally 2026 — Round 3",
  "serverName": "Example League",
  "expiresAt": "2026-08-16T18:00:00Z",
  "authorizationId": "01K2D80A7VBZJ2R9X4Z8DF6M3P",
  "eventSettings": { "penaltyCapPct": 50, "dnfNoRejoin": true }
}
```

| Champ | Obligatoire | Règle |
|---|---|---|
| `schemaVersion` | non | `acr-pairing-response/1.<mineure>`. |
| `submissionToken` | oui | Chaîne opaque non vide. |
| `submissionUrl` | oui | `https`, **même origine** (schéma, hôte, port) que `serverUrl`. |
| `eventId` | oui | **Chaîne**, 1 à 128 caractères. Renvoyée telle quelle. |
| `eventName` | non | Affichage seulement. |
| `serverName` | non | Affichage seulement : nom lisible du serveur, utile pour une adresse IP. |
| `expiresAt` | non | ISO 8601 UTC. Le jeton est supprimé une fois la date passée. |
| `authorizationId` | non | Identifiant non secret, affiché pour le support. |
| `eventSettings` | non | Réglages que le serveur propose pour cette épreuve (ci-dessous). |

- Les champs inconnus sont ignorés. `event.eventId` / `event.name` sont acceptés à
  la place de `eventId` / `eventName`.
- Une réponse qui enfreint une règle ci-dessus est un échange raté ; rien n'est
  enregistré.
- `eventName` et `serverName` sont du texte brut, sans caractères de contrôle,
  tronqués avec « … » au-delà de 200 caractères (jamais un échec). Dans
  l'application, `serverName` est toujours affiché **à côté** de l'hôte, jamais à
  sa place : `Example League · 203.0.113.10:8443 — Summer Rally 2026 — Round 3`.

**Réglages de l'épreuve.** `eventSettings` regroupe ce que le serveur propose pour
son épreuve, pour que le tableau, la page et Discord suivent ses règles. La v1
définit :

| Champ | Règle |
|---|---|
| `penaltyCapPct` | Nombre, de 0 à 500 : le pourcentage du *Penalty cap*. |
| `dnfNoRejoin` | Booléen : l'option *DNF – No Rejoin*. |

- Chaque champ est facultatif ; un champ invalide est ignoré et journalisé, jamais
  un échange raté. Les champs inconnus sont ignorés : des versions mineures
  ultérieures pourront en ajouter (heure de départ, voiture…), toujours comme des
  suggestions montrées à l'opérateur, jamais comme un contrôle qui bloque un envoi.
- À un échange réussi, les valeurs présentes sont appliquées au panneau Config et
  journalisées (*"Penalty cap set to 50 % by Example League."*). Réutiliser une
  association enregistrée n'applique rien.
- L'opérateur peut les modifier à tout moment. Tant qu'un réglage diffère de celui
  proposé par une association active, un petit avertissement s'affiche à côté, avec
  le nom de cette association en infobulle.
- Ils sont enregistrés avec l'association. Ils ne changent que le tableau ;
  l'envoi contient des faits bruts dans tous les cas.

| Résultat de l'échange | Client |
|---|---|
| `2xx` valide | association créée (§ 3) |
| `400`, `401`, `403`, `404`, `409`, `410`, `422` | *"Key already used or expired — ask for a new one."* |
| `429` | *"Too many attempts — try again later"*, avec le délai de `Retry-After` |
| erreur réseau, délai dépassé, `5xx` | *"Server unreachable — try again"*, clé conservée |
| `3xx` | erreur : les redirections ne sont jamais suivies, ici comme à l'envoi |

**Clé recollée.** Une clé est reconnue par `serverUrl` + `pairingCode`. Une clé
déjà échangée réutilise son association sans requête, sauf si le serveur a refusé
son jeton (`401`/`403`) : le code est alors échangé à nouveau et le serveur
décide. Un échange raté ne supprime jamais une association qui a encore un jeton
(*"Existing association kept."*).

**Stockage.** Le jeton est chiffré avec DPAPI (`CurrentUser`, identifiant de
l'association comme entropie) dans `ACRLiveTiming.pairings`, à côté du fichier de
config, avec le reste de l'association, et survit au redémarrage. Il n'apparaît
jamais dans le fichier de config, le journal, un message d'erreur, l'interface ou
la page web. Le code d'association n'est pas stocké, seulement un SHA-256 de
`serverUrl` + `pairingCode`.

## 3. Associations

Une association est un échange réussi : origine du serveur, `eventId`, jeton, et
l'instant où l'échange a répondu (pour `startedAfterPairing`, § 5).

Le serveur décide de l'état ; l'opérateur choisit seulement si *Send…* envoie à
une association :

| Affiché | État | Reçoit les envois | Sur la page web |
|---|---|---|---|
| ▶ | `active`, pas en pause | oui | oui |
| ⏸ | `active`, mise en pause par l'opérateur | non | non |
| ⏹ | `ended` : le serveur a répondu `ended`. Le jeton sert encore aux corrections jusqu'à la fermeture de l'application : cliquer ⏹ lui renvoie (▶). | non, sauf clic | non |
| ✕ | `refused` ou `expired` : jeton supprimé ; listée jusqu'à la fermeture de l'application | jamais | non |

Une nouvelle association commence en ▶. Cliquer ▶ la met en pause, cliquer ⏸ la
relance.

**Signal du serveur.** Le serveur ne contacte jamais le client ; il agit par ses
réponses. Un accusé de réception peut porter `"association": "active"` ou
`"ended"`. **Seul `"ended"` termine l'association ; absent, inconnu ou illisible
vaut `active`.** `active` remet une association terminée à `active`. Un jeton
révoqué se manifeste par un `401`/`403` au prochain envoi.

**Suppression du jeton**, au premier de :

1. `401`/`403` sur un envoi (`refused`) ;
2. `expiresAt` dépassé (`expired`) ;
3. fermeture de l'application alors que l'association est `ended` ;
4. *Remove* par l'opérateur : l'association quitte la liste immédiatement.

Seules les associations `active` sont conservées d'un lancement à l'autre.

**Dernier envoi.** Chaque ligne d'association affiche la réponse à son dernier
envoi jusqu'à la fermeture de l'application, par exemple
*21:42 — Summer Rally — Stored: 14 drivers, 6 stages.* (§ 6).

**Page web.** La page et `/state` affichent les épreuves auxquelles les résultats
sont envoyés — les associations en ▶ qui sont `active` — sous la forme
`serverName — eventName`, avec l'hôte (`IdnHost`, jamais le port) à la place d'un
`serverName` vide, signalées *🏆 Counts for*. Jamais l'URL, le jeton ni la
clé.

**Noms de groupe.** Un groupe créé dans le panneau Stages alors qu'une association
est active prend l'`eventName` de la première association active au lieu de
`Rally N`.

## 4. Interface opérateur

Un panneau **Servers**, à côté du panneau Discord.

- **Associations** : une ligne chacune — le bouton d'état (▶ ⏸ ⏹ ✕, § 3), puis
  `serverName · hôte — eventName`, son état et son dernier envoi (le message du
  serveur, en entier dans une infobulle), et un bouton de suppression (*Remove*,
  qui demande confirmation : renvoyer à cette épreuve demandera une nouvelle clé).
- **Clé d'association** : le champ de clé (son texte indicatif le dit) ; l'hôte
  décodé (ou ce qui ne va pas dans la clé) s'affiche dessous dès qu'elle est
  collée, et *Pair* fait l'échange.
- **Envoi** : un sélecteur avec *All stages* et chaque groupe de rallye (comme pour
  Discord) et un seul bouton : **Send…** quand au moins une association est en ▶,
  **Save…** (fichier JSON, § 7) quand aucune ne l'est. Les deux ouvrent d'abord
  l'aperçu. Un bouton dossier ouvre le dossier `Results`.

Chaque envoi et sa réponse sont aussi écrits dans le journal d'activité (statut et
message, jamais le jeton ni le contenu).

## 5. Contenu envoyé

```http
POST {submissionUrl}
Content-Type: application/json; charset=utf-8
Authorization: Bearer <submissionToken>
User-Agent: ACRLiveTiming/<appVersion>
```

```json
{
  "schemaVersion": "acrlt-results/1.0",
  "eventId": "summer-2026-r03",
  "runId": "5f0c2a9e-8d3b-4e71-a6c4-2b9d71e0f3a8",
  "generatedAt": "2026-08-15T20:42:12.418Z",
  "submissionMode": "operator-confirmed",
  "appVersion": "1.4.0",
  "group": 1,
  "groupName": "Summer Rally",
  "stages": [
    { "order": 1, "routeId": "AlsaceS4SaverneFullForward", "name": "Saverne",
      "lengthKm": 9.2, "startedAfterPairing": true,
      "startedAt": "2026-08-15T20:05:31.204Z",
      "inGameStartTime": "17:40", "weather": "Light rain · 12°C" },
    { "order": 2, "routeId": "WelesS3HafrenNorthFullForward", "name": "Hafren North",
      "startedAfterPairing": true, "startedAt": "2026-08-15T20:21:09.877Z" }
  ],
  "participants": [
    {
      "steamId64": "76561190000000001",
      "eosPuid": "0002aaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "usernameAtRun": "Driver One",
      "nation": "France",
      "nationName": "France",
      "stages": [
        { "order": 1, "status": "finished", "timeMs": 462871, "penaltyMs": 10000,
          "sectorsMs": [151034, 151078, 150759],
          "carId": "SkodaFabiaRSRally2", "carName": "Škoda Fabia RS Rally2" },
        { "order": 2, "status": "dnf", "timeMs": null, "sectorsMs": [98410],
          "carId": "SkodaFabiaRSRally2", "carName": "Škoda Fabia RS Rally2" }
      ]
    },
    {
      "steamId64": null,
      "eosPuid": null,
      "usernameAtRun": "Driver Two",
      "nation": "SwitzerlAnd",
      "nationName": "Switzerland",
      "stages": [
        { "order": 1, "status": "finished", "timeMs": 460002, "penaltyMs": 0,
          "sectorsMs": [153300, 153400, 153302],
          "carId": "HyundaiI20NRally2", "carName": "Hyundai i20 Rally2" },
        { "order": 2, "status": "not_started", "timeMs": null }
      ]
    }
  ]
}
```

Un envoi contient **une sélection** : un groupe de rallye, ou *All stages*.

### Enveloppe

| Champ | Règle |
|---|---|
| `schemaVersion` | `acrlt-results/1.0`. |
| `eventId` | Celui de l'échange, tel quel. |
| `runId` | UUID aléatoire de la sélection, le même pour chaque envoi et chaque destination : créé avec le groupe, ou pour *All stages* au démarrage ou à la remise à zéro de la session. |
| `generatedAt` | UTC, ISO 8601, millisecondes. |
| `submissionMode` | `operator-confirmed`. |
| `appVersion` | Version d'ACR Live Timing. |
| `group` | Identifiant du groupe chez l'hôte, `0` pour *All stages*. Local à la session et réutilisé : identifier par `runId`. |
| `groupName` | Nom du rallye chez l'hôte. Absent pour *All stages*. |

### `stages[]`

Les spéciales de la sélection qui ont des données, dans l'ordre de course : toutes celles du groupe de rallye, cochées ou non, ou pour *All stages* celles cochées sur le tableau.

| Champ | Règle |
|---|---|
| `order` | 1, 2, 3… Les entrées des participants s'y réfèrent. |
| `routeId` | Le parcours **tel que le jeu l'envoie sur le réseau** (`WelesS3HafrenNorthFullForward`), comme dans les sauvegardes du jeu. Jamais l'identifiant normalisé du catalogue. `null` s'il n'a jamais été reçu. |
| `name`, `lengthKm` | Tirés du catalogue de contenu, pour l'affichage. `name` vaut l'identifiant brut à défaut ; `lengthKm` absent si inconnu. |
| `startedAfterPairing` | `true` si la spéciale a démarré après l'association **de cette destination**. |
| `startedAt` | Départ en UTC, d'après l'horloge Windows. Absent si le départ n'a pas été vu. |
| `inGameStartTime` | Heure du jeu au départ, `HH:mm`, si reçue. |
| `weather` | Libellé météo comme sur la page (`Light rain · 12°C`), si reçu. |

`startedAfterPairing` : une spéciale démarre quand le client voit son départ (la
séquence de départ qui ouvre sa colonne sur le tableau ; une spéciale rejointe en
cours de route n'est jamais sur le tableau). Départs et associations sont ordonnés
avec une horloge monotone ; une association faite lors d'un lancement précédent de
l'application précède toutes les spéciales du lancement actuel. La valeur est
calculée par destination à la construction de l'envoi, jamais stockée avec la
spéciale. Une spéciale dont le départ n'a pas été vu vaut `false`.

### `participants[]`

Les pilotes qui ont une entrée sur au moins une des spéciales envoyées, sans ordre.

| Champ | Règle |
|---|---|
| `steamId64` | Chaîne décimale, ou `null` si l'identité n'a jamais été reçue. |
| `eosPuid` | Identifiant utilisateur EOS, ou `null` de même. |
| `usernameAtRun` | Nom affiché, pour l'audit seulement — jamais pour identifier. |
| `nation` | Le jeton tel que le jeu l'envoie (`France`), ou `null` s'il est inconnu. Envoyé même avec *Hide nationalities* : cette option ne les masque que sur le tableau. |
| `nationName` | Son nom d'après le catalogue de contenu, le jeton à défaut, `null` avec `nation`. Pour l'affichage seulement. |
| `stages` | Une entrée par spéciale de premier niveau, même `order`. |

Un SteamID64 ou un EOS PUID apparaît au plus une fois par envoi.

| `status` | Signification | Champs |
|---|---|---|
| `finished` | Arrivée confirmée par la voiture du pilote ou le résultat de session du jeu. | `timeMs`, `penaltyMs`, `sectorsMs`, `carId`, `carName` |
| `dnf` | Abandon, disqualification ou départ de la spéciale (le jeu ne permet pas de les distinguer de façon fiable). | `timeMs: null`, `sectorsMs` des secteurs passés (éventuellement vide), `carId`, `carName` |
| `not_started` | Aucune entrée sur cette spéciale. | `timeMs: null` |

- `sectorsMs` : le temps de chaque secteur en millisecondes entières, hors
  pénalité. Ce sont les différences entre les temps cumulés du jeu arrondis à la
  milliseconde : leur somme donne exactement le temps de la spéciale, et un secteur
  peut différer d'1 ms du temps de secteur affiché par le jeu.
- `penaltyMs` : la pénalité appliquée par le jeu, en millisecondes.
- `timeMs` = somme des `sectorsMs` + `penaltyMs`, exactement : le temps affiché
  sur le tableau.
- `carId` : la voiture **telle que le jeu l'envoie** (`SkodaFabiaRSRally2`), ou
  `null`. `carName` : son nom d'après le catalogue de contenu, l'identifiant brut à
  défaut ; pour l'affichage seulement.
- **La casse varie.** Les noms de voiture, de parcours et de nation sont
  enregistrés sur le client de chaque joueur : `HyundaiI20NRally2` et
  `Hyundaii20NRally2` ont tous deux été vus. Le client envoie ce qu'il a reçu ; les
  serveurs comparent sans tenir compte de la casse.

Jamais envoyés : positions, totaux, écarts, points, plafond de pénalité, option
*No rejoin*, DSQ, numéro de révision.

## 6. Envoi

### Aperçu

*Send…* et *Save…* ouvrent d'abord un **aperçu** : une fenêtre de confirmation
qui liste les spéciales et les pilotes de la sélection, les destinations, les
avertissements et ce qui bloque, avec *Confirm* et *Cancel*. Rien n'est envoyé ni
écrit avant *Confirm*.

L'aperçu refuse, avec la raison, tant que :

- une spéciale de la sélection est en cours (`Racing` ou `Finishing`) ;
- une cellule est **non concluante** (pilote encore sur la spéciale, arrivée non
  confirmée, intermédiaire sans issue) — chaque cas est listé ; attendre le
  résultat de session, ou écarter la spéciale (la décocher pour *All stages*, la
  dégrouper pour un rallye) ;
- il ne reste aucune spéciale ou aucun participant.

Avertissements, jamais bloquants : pilotes sans identité, spéciales sans `routeId`,
entrées sans `carId`, spéciales avec `startedAfterPairing: false` (par
destination). L'aperçu se termine par *"N drivers: their SteamID/EOS IDs will be
sent to ‹host›."* pour chaque destination.

### Destinations

Une confirmation construit **un envoi par association en ▶** et les envoie
ensemble. Ils partagent `runId`, `group`, `groupName`, `stages` et `participants` ;
chacun a son propre `eventId`, son `startedAfterPairing` et sa réponse. L'échec
d'une destination n'affecte pas les autres.

- Une requête par envoi, avec un délai de 30 s. Une association ne peut pas
  recevoir de nouvel envoi tant que sa requête n'a pas répondu ou expiré.
- Renvoyer, corrigé ou à l'identique, est simplement un nouvel envoi. Le serveur
  garde le dernier par `eventId` + `runId` ; `generatedAt` distingue les envois.
- Chaque envoi est enregistré en fichier JSON (§ 7). Rien n'est renvoyé
  automatiquement, même après un redémarrage.

### Réponses

| HTTP | Le dernier envoi affiche |
|---|---|
| `2xx` | *Stored — ‹message du serveur›*, ou *Stored* ; `association` appliqué (§ 3) |
| erreur réseau, délai dépassé | *Server unreachable — send again.* |
| `429` | *Too many requests*, avec le délai de `Retry-After` |
| `401`, `403` | *Association refused — a new key is needed.* ; jeton supprimé |
| tout autre | *Refused (HTTP n)*, avec le message du serveur |

Les deux types de réponse peuvent porter un corps JSON :

```json
{ "association": "active", "message": "Stored: 14 drivers, 6 stages." }
```

```json
{ "error": "event_closed", "message": "This event no longer accepts results." }
```

Le client affiche `message`, sinon `error`, sinon le début d'un corps non JSON :
texte brut, sans caractères de contrôle, 300 caractères au plus. Un accusé de
réception signifie « enregistré », pas « validé ».

## 7. Fichiers JSON

Les résultats sont toujours disponibles en fichier, avec ou sans serveur.

- **Copie de chaque envoi.** Chaque envoi est aussi écrit, à l'octet près, dans le
  dossier `Results` à côté du fichier de config, quelle que soit la réponse :
  `yyyyMMdd-HHmmss_<hôte>_<eventId>.json` (caractères interdits dans un nom de
  fichier remplacés par `_`). Le panneau a un bouton pour ouvrir le dossier.
  L'application ne supprime jamais ces fichiers. Un fichier qui ne peut pas être
  écrit n'empêche pas l'envoi ; le journal le signale.
- **Save…** Quand aucune association n'est en ▶, le bouton est **Save…** : il
  écrit la sélection dans un fichier choisi par l'opérateur, sans aucune requête.
  Le document est le même envoi `acrlt-results/1.0` sans les champs propres à une
  destination (`eventId`, `startedAfterPairing`), avec
  `submissionMode: "file-export"`. Mêmes contrôles d'aperçu que *Send*.

Ces fichiers contiennent les SteamID et EOS ID en clair ; l'aperçu le signale.

## 8. Sécurité et confidentialité

- TLS avec validation du certificat, toujours, sauf pour les adresses `http` de
  test du § 1, réservées aux builds Debug ; pas de redirection.
- Le jeton ne part que dans `Authorization`, et seulement vers `submissionUrl`.
- SteamID64 et EOS PUID ne quittent la machine que dans un envoi confirmé par
  l'opérateur, vers un serveur dont il a collé la clé. En local, ils ne sont écrits
  que dans les fichiers JSON du § 7. Ils n'apparaissent jamais sur la page web,
  dans `/state`, dans les exports copie/CSV/PNG, sur le tunnel public ni dans les
  messages Discord.
- Aucune requête avant qu'une clé soit collée et *Pair* cliqué ; ensuite, seulement
  l'échange et les envois.
- Les données personnelles qu'un serveur reçoit (pseudos, nations, SteamID64, EOS
  PUID) relèvent dès lors de la responsabilité de son exploitant : leur
  conservation, leur usage, leur publication et leur suppression suivent les règles
  et la politique de confidentialité de ce serveur. Les auteurs d'ACR Live Timing
  n'en reçoivent rien.

## 9. Checklist serveur

1. Codes d'association liés à une épreuve, de courte durée, avec un nombre de
   tentatives limité.
2. HTTPS avec un certificat valide pour l'hôte de la clé.
3. Un jeton à forte entropie limité à l'épreuve ; n'en stocker que le hash.
4. `submissionUrl` sur la même origine ; `eventId` une chaîne de 128 caractères au
   plus.
5. `2xx` seulement une fois l'envoi enregistré durablement.
6. `association: "ended"` dans l'accusé de réception qui clôt l'épreuve
   (`"active"`, ou rien, la laisse ouverte), et un court `message` pour
   l'opérateur dans chaque réponse.
7. `400` pour un `schemaVersion` inconnu.
8. Identifier les pilotes par `steamId64` / `eosPuid`, jamais par `usernameAtRun`.
9. Traiter `routeId`, `carId` et `nation` comme des noms bruts du jeu : comparer
   sans tenir compte de la casse, conserver ceux qui sont inconnus.
10. Garder le dernier envoi par `eventId` + `runId` (`generatedAt` le plus récent).
    Le client peut renvoyer le même contenu, après un délai dépassé ou à la demande
    de l'opérateur ; ce n'est pas une erreur.
11. Facultatif : utiliser `runId` pour repérer un même rallye envoyé deux fois, et
    `startedAfterPairing` / `startedAt` pour refuser une association trop tardive.
12. Facultatif : renvoyer `eventSettings` pour que le tableau de l'hôte suive les
    règles de l'épreuve (plafond de pénalité, DNF – No Rejoin).
