# ACR Live Timing

[![build](https://github.com/FlorentDChamps/acr-live-timing/actions/workflows/build.yml/badge.svg)](https://github.com/FlorentDChamps/acr-live-timing/actions/workflows/build.yml)

**Live timing et classement partagé pour les lobbies multijoueur d'Assetto Corsa Rally.**

*Lire ce document en [anglais / English](README.md).*

ACR n'affiche les résultats de spéciale qu'à l'intérieur du lobby, sur l'écran de
chaque joueur. Cette application décode passivement le trafic réseau du jeu sur le PC
d'un joueur et le transforme en **page web de classement live** — temps de chaque
arrivant (pénalités incluses), drapeau de nationalité et voiture — que tout le lobby
peut suivre dans un navigateur, spéciale après spéciale, avec les totaux de session.
Partageable sur le LAN, ou publiable pour n'importe qui via un lien Cloudflare Tunnel
en un clic.

![La page de classement live pendant une spéciale](docs/screenshot.png)

<sub>Données de démonstration — pseudos fictifs, adresse de serveur d'exemple.</sub>

> ⚠️ **Outil d'analyse passif, à usage personnel/éducatif.** Il ne fait que *lire*
> le trafic des lobbies où vous jouez. Il ne modifie rien, n'injecte rien, et ne
> donne aucun avantage de pilotage.

> **Projet amateur non officiel.** Sans aucune affiliation ni approbation de Kunos
> Simulazioni, Supernova Games Studios ou 505 Games. *Assetto Corsa* est une marque
> de ses propriétaires respectifs ; le nom n'est utilisé ici que pour indiquer la
> compatibilité. L'outil lit **uniquement du trafic en clair** et ne tentera jamais
> de déchiffrer, contourner ou neutraliser une quelconque protection.

---

## Fonctionnalités

- **Zéro configuration** — rejoignez un lobby ACR ; le serveur de jeu est
  auto-détecté par signature protocole (IP et port changent à chaque lobby, rien
  n'est codé en dur).
- **Résultats de spéciale en direct** — pseudos, temps finaux validés par les splits
  (exacts au ms près vs l'écran en jeu, pénalités incluses), nom de la spéciale,
  drapeau de nationalité et modèle de voiture. La liste de voitures affichée suit les
  spéciales sélectionnées : un changement de voiture entre spéciales est conservé et
  les doublons sont retirés. Pendant qu'une spéciale se court (phases Course à
  Spectateur, retour à l'historique aux résultats), avec **Hide split times**
  désactivé (valeur par défaut), le tableau conserve le total à gauche et remplace
  l'historique des spéciales par les splits cumulés de chaque
  secteur puis le temps final de la spéciale. Chaque colonne affiche l'écart au
  meilleur temps et son top 3 ; les lignes suivent automatiquement le classement du
  dernier split disponible. L'historique habituel réapparaît aux résultats.
- **Classement de session** — une colonne par run de spéciale, totaux et rangs.
  *Plafond de pénalité* configurable : un pilote absent d'une spéciale est compté au
  `temps finalisé le plus lent × (1 + plafond de pénalité %)` et signalé.
  Chaque spéciale peut être exclue des totaux d'une simple case à cocher. Une spéciale
  rejointe **en cours** n'est **pas** comptée — son départ a été manqué — mais son nom
  et les liaisons pilote/voiture sont quand même appris, pour que la spéciale suivante,
  capturée depuis le début, soit prête dès le premier split. Les colonnes sont
  triables (n'importe quelle spéciale ou le total), et des flèches de progression à
  côté de chaque pilote montrent son mouvement sur la dernière spéciale.
- **Rallyes, classement général & points de championnat** — regroupez les spéciales
  en rallyes nommés depuis le panneau hôte (*Stages — select to group* : cochez les
  spéciales, cliquez **Group**, renommez sur place). La page web gagne un onglet
  **Standings** : un classement indépendant par rallye plus des points de championnat
  (barème 25-18-15-12-10-8-6-4-2-1 par rallye), triable par rallye ou par total de
  points ; le tableau principal gagne un filtre pour n'afficher que les spéciales
  d'un rallye. Le groupement est défini par l'hôte et partagé avec tous les
  spectateurs.
- **Exports de résultats** — trois boutons sur la page exportent exactement ce qui
  est affiché : copie en texte, CSV (prêt pour Excel, séparateur selon la locale) et
  image PNG. Côté hôte, un **webhook Discord** optionnel (panneau Discord) poste dans
  votre salon en un clic : une alerte avec le lien live, le tableau des points ou les
  résultats d'un rallye en image. L'URL du webhook est stockée chiffrée, lisible
  uniquement par votre compte Windows.
- **Affichage à l'arrivée** — dans l'historique des spéciales, le temps final d'un
  pilote n'est validé qu'une fois la ligne d'arrivée franchie. La détection est
  événementielle et exacte : le jeu
  réplique une phase de course par voiture (`RaceStateData.Phase`) ; le passage à
  *Ended* marque l'arrivée à l'instant précis, le chronomètre gelé correspondant au
  résultat affiché à la milliseconde. Streamée paquet par paquet : un replay révèle
  la même progression qu'en live, quelle que soit la vitesse de lecture. Une
  accroche de l'outil en cours de session ré-identifie le flux chrono à sa forme
  réseau — aucun redémarrage de spéciale nécessaire ; seules les captures réellement
  sans chrono (anciens enregistrements) se replient sur le gating par splits
  complets. **Hide split times** est désactivé par défaut : le décocher affiche la vue
  de secteurs en direct pendant la course ; le cocher conserve l'ancien tableau des
  spéciales et masque les temps intermédiaires jusqu'à l'arrivée.
- **Affichage des DNF** — un pilote qui n'a jamais franchi la ligne est affiché
  **DNF** sur cette spéciale (compté au temps plafond de pénalité, comme une
  absence). Sur la spéciale en cours, il apparaît dès que la phase de course de la
  voiture passe à *Retire/Disqualify* ; à la clôture d'une spéciale, le sort de
  chaque voiture est mémorisé (abandon, disqualification ou disparition en cours de
  run), donc les spéciales passées gardent un état DNF exact, avec la déduction
  « un autre a fini, pas lui » en repli pour les trous de capture. Le drapeau
  d'abandon des résultats répliqués par le jeu est aussi décodé directement : même
  un pilote qui quitte **avant le premier split** est listé DNF — sans temps,
  puisqu'il n'en a jamais posé — au lieu de disparaître silencieusement. La règle
  optionnelle **DNF – No Rejoin** rend ce premier abandon définitif pour le rallye :
  les temps des spéciales suivantes restent visibles mais ne contribuent plus à un
  total général. Tous les finishers précèdent les DNF ; ceux-ci sont départagés par
  le nombre de spéciales terminées avant le premier abandon, puis par leur temps réel
  cumulé sur ces seules spéciales. Un pilote sans aucun départ dans le rallye est DNS.
  Ce mode remplace le plafond de pénalité pour les spéciales sélectionnées, tandis
  que les DNF classés restent éligibles aux points de championnat.
- **Progression de spéciale en direct** — une piste horizontale au-dessus du tableau
  montre la position live de chaque pilote sur la spéciale en cours. Par défaut la
  piste couvre une **portée fixe configurable** derrière le leader (échelle stable) ;
  décocher *Fixed* dans le panneau Config bascule sur une fenêtre auto-ajustée entre
  le leader et la dernière voiture en course, plafonnée à cette portée. Chaque
  marqueur est nommé **dès le spawn, depuis
  la ligne de départ** : l'acteur PlayerState propriétaire de la voiture re-réplique
  son bloc d'identité (steamid + pseudo) à chaque spéciale, et le décodeur le lie de
  façon déterministe au composant de télémétrie de la voiture. La correspondance par
  temps de secteur reste en repli (ex. accroche de l'outil en cours de spéciale) :
  une voiture pas encore identifiée apparaît en point anonyme jusqu'à son premier
  split. Pendant la **première spéciale** après l'accroche de l'outil, les données
  peuvent donc être partielles : certains marqueurs peuvent rester anonymes un
  moment et le tableau se remplit au fil des premiers splits. Tout est complet dès
  la spéciale suivante.
- **En-tête lobby & spéciale** — l'en-tête de la page affiche la phase de lobby en
  cours (Chargement, Course, Résultats, Parc d'assistance…), l'**heure de départ en
  jeu** de la spéciale (heure du jour) et sa **prévision météo**, le tout décodé
  depuis la timeline météo répliquée et la machine à états du lobby.
- **Vue web partageable** — serveur web embarqué pour le LAN, plus un lien public
  `https://….trycloudflare.com` optionnel. Le binaire `cloudflared` est épinglé à une
  version précise, téléchargé une seule fois et **vérifié SHA-256** contre le
  checksum publié par Cloudflare avant toute exécution. Les boutons Start / Publish
  servent aussi de boutons d'arrêt, et le lien public a un bouton de copie en un clic.
- **Réglages d'affichage par spectateur** — un bouton roue crantée sur la page web
  ouvre un panneau qui reprend **localement** les réglages de classement/affichage de
  l'hôte : exclure des spéciales du total, changer le plafond de pénalité %, régler la
  fenêtre de progression ou basculer sa portée fixe, masquer les nationalités,
  activer/couper **Hide split times**, filtrer par rallye, trier par n'importe quelle
  colonne et basculer le thème clair/sombre de la page.
  Modifier une spéciale/pénalité/gating **recalcule le tableau dans le navigateur** à
  partir des données brutes par spéciale que la page reçoit déjà — sans jamais toucher
  la vue de l'hôte ni celle des autres spectateurs. Les réglages sont propres à la
  session (un rechargement les annule) et un bouton **Reset to host config** restaure
  la configuration publiée par l'hôte. Masqué dans les modes overlay OBS. Enregistrer
  la page via le **Enregistrer sous** du navigateur fige le classement courant : le
  fichier sauvé s'ouvre hors-ligne (sans serveur), panneau de réglages toujours
  fonctionnel.
- **Titre & description de page + aperçu de lien** — définissez un titre et une
  description optionnels (panneau en haut à gauche) affichés en haut de la page
  partagée ; les URL saisies dans l'un ou l'autre sont cliquables. Des métadonnées
  Open Graph / Twitter Card sont aussi émises : coller le lien dans Discord, Facebook,
  WhatsApp… affiche ce titre et cette description en aperçu enrichi.
- **Overlays streaming (OBS)** — deux fenêtres indépendantes, redimensionnables et
  transparentes — l'une avec le **classement général**, l'autre avec la **barre de
  progression en direct** — qui réutilisent la page web. Chacune se règle séparément
  dans le panneau *Overlays* : afficher/masquer, *always-on-top*, *lock* click-through,
  et **opacité du fond** (par pas de 10 %). Redimensionner = zoomer, puis posez-les
  directement par-dessus le jeu en overlay à l'écran pendant que vous roulez. Pour
  OBS, capturez-les plutôt en **Browser Source** — le panneau *Overlays* a un bouton
  *Copy OBS source URL* par widget (voir [Capture avec OBS](#capture-avec-obs)).
  (Lancez d'abord le serveur web local — les overlays s'y chargent. Un jeu en fenêtré /
  plein écran fenêtré est requis pour qu'un overlay bureau passe devant ; le plein
  écran exclusif ne peut pas être recouvert.)
- **Clair ou sombre, partout** — l'application reprend le réglage clair/sombre de
  Windows au lancement, avec un interrupteur dans l'en-tête pour basculer à tout
  moment. La page web suit elle aussi le thème système de chaque spectateur, avec son
  propre bouton soleil/lune.
- **Notification de mise à jour au démarrage** — une vérification légère des releases
  de ce dépôt ; si une version plus récente existe, une fenêtre pointe vers elle
  (rien n'est téléchargé sans votre confirmation) et peut être ignorée par version.
- **Enregistrement & replay** — enregistrement optionnel de la session dans un
  `.pcap` standard (lisible dans Wireshark), rejouable ensuite dans le même pipeline
  de décodage.
- **Mémorise votre configuration** — tout l'état de la fenêtre (options,
  titre/description de page, le webhook Discord — chiffré — et pour chaque overlay :
  position, taille, always-on-top, lock et opacité) est sauvegardé dans un fichier
  `ACRLiveTiming.config` à côté de l'exe et restauré au prochain lancement.
- **Aucun driver de capture** — raw socket Windows au lieu de Npcap/WinPcap ; seule
  exigence : lancer en Administrateur.

## Vie privée & sécurité — ce que fait réellement l'application

L'application demande des droits Administrateur et touche au réseau ; voici le
tableau complet, sans détour :

- **Pourquoi Administrateur ?** Lire les paquets entrants sans driver de capture
  exige un raw socket Windows (`SIO_RCVALL`), que Windows ne donne qu'aux processus
  élevés. L'élévation ne sert qu'à ça.
- **Qu'est-ce qui est capturé ?** Le trafic UDP entrant de *votre propre* machine,
  en mémoire. Une fois le serveur ACR détecté, seuls ses paquets sont décodés ; tout
  le reste est ignoré. Rien n'est écrit sur disque sauf si *vous* activez
  l'enregistrement pcap (fichier local que vous choisissez). Attention : le raw
  socket voit **tout** le trafic entrant, donc un `.pcap` de debug contient plus que
  les paquets du jeu — traitez les fichiers de capture comme sensibles et
  réfléchissez avant d'en partager un.
- **Qu'est-ce qui est décodé ?** Exactement ce que le jeu affiche déjà dans le
  lobby : pseudos, temps de spéciale, pénalités, nom de la spéciale, nationalité,
  modèle de voiture. Le jeu réplique aussi des identifiants de compte stables
  (Steam/EOS) ; l'application ne s'en sert qu'en interne, pour garder les résultats
  d'un pilote sur une seule ligne malgré un changement de pseudo — ils ne sont
  jamais affichés, jamais publiés sur la page et jamais inclus dans un export.
  Aucun champ de nom réel n'est lu.
- **Qu'est-ce qui sort de votre machine ?** Rien, par défaut. Aucune télémétrie,
  aucun envoi. Seules connexions sortantes : (1) une vérification légère au démarrage,
  auprès de GitHub, d'une éventuelle nouvelle release d'ACR Live Timing — aucun
  téléchargement sans confirmation de votre part dans sa fenêtre ; (2) le
  téléchargement unique du binaire `cloudflared` épinglé et vérifié par checksum,
  depuis les releases GitHub officielles de Cloudflare — effectué automatiquement en
  arrière-plan au premier lancement, même si vous ne publiez jamais ; (3) le tunnel
  Cloudflare lui-même, *uniquement si vous cliquez Publish* — à partir de là, la page
  de classement est accessible à quiconque a le lien, jusqu'à fermeture de
  l'application. Si l'application meurt sans fermeture normale (crash, arrêt via le
  Gestionnaire des tâches), le process du tunnel peut lui survivre — vérifiez
  `cloudflared.exe` dans le Gestionnaire des tâches ; (4) un message vers Discord,
  *uniquement si* vous collez une URL de webhook dans le panneau Discord et cliquez
  un de ses boutons — il poste l'alerte ou l'image de résultats dans ce salon, rien
  d'autre. (Les navigateurs qui consultent la page chargent aussi les drapeaux depuis
  flagcdn.com.)
- **Qui peut voir la page locale ?** Le serveur web embarqué écoute sur toutes les
  interfaces réseau : n'importe qui sur le même LAN/Wi-Fi peut ouvrir la page tant
  que le serveur tourne (c'est le but — la partager avec le lobby). Arrêtez le
  serveur (re-cliquer **Start**) quand vous avez terminé.
- **Ce qu'elle ne fera jamais :** envoyer des paquets au serveur de jeu, modifier
  les fichiers ou la mémoire du jeu pendant la partie, ou interagir avec le process
  du jeu de quelque façon. C'est un écouteur, rien d'autre.

### Publier un lien public — données des autres joueurs

Le classement affiche les pseudos des autres pilotes et, par défaut, leur
nationalité. Ce sont des **données personnelles de tiers**. Sur votre LAN, entre les
personnes avec qui vous jouez déjà, aucun souci. Mais le bouton **Publish** les
expose sur l'internet public à quiconque a le lien — une responsabilité différente :

- Le lien public n'est jamais automatique : c'est un clic délibéré, et l'application
  demande confirmation avant de l'ouvrir.
- L'option **« Hide nationalities (shared view) »** (panneau Config) retire les
  drapeaux de nationalité de la page partagée ; pseudos et temps restent affichés.
- Si vous publiez, ne partagez le lien qu'avec les personnes concernées, et fermez
  l'application (ou le tunnel) une fois terminé. Rien n'est stocké : à la fermeture,
  la page disparaît.

## Fonctionnement

```
Trafic UDP ──> sniffer raw socket ──> auto-détection serveur
                                             │
                                             ▼
              décodage réplication Unreal Engine (en clair)
  pseudos · splits · pénalités · spéciale · nation · voiture · phase/chrono course
     distance spline · phase lobby · heure de départ · météo
                                             │
                                             ▼
              matrice de session ──> serveur web embarqué ──> navigateur
                                            │
                                            └──> Cloudflare quick tunnel (optionnel)
```

ACR tourne sur Unreal Engine ; son trafic multijoueur est de la réplication UE
standard, actuellement non chiffrée dans le sens serveur→client. L'application lit ce
flux en clair pour restituer exactement les valeurs que le jeu affiche déjà dans le
lobby. Rien du binaire du jeu n'est lu, modifié, patché ni redistribué.

## Démarrage

### Prérequis

- Windows 10/11 x64
- [Runtime .NET 8 Desktop](https://dotnet.microsoft.com/download/dotnet/8.0)
  (le SDK pour compiler depuis les sources). S'il manque, Windows affiche au
  premier lancement de l'exe une boîte de dialogue avec le lien de
  téléchargement direct — installez-le une fois, puis relancez.
- Droits Administrateur (capture raw socket)

### Compilation

```
dotnet build        # build debug
publish.bat         # exe Release monofichier dans .\publish\
```

### Utilisation

1. Lancez `ACRLiveTiming.exe` **en Administrateur**.
2. Rejoignez un lobby multijoueur ACR — le voyant d'état passe au vert dès que le
   serveur est détecté (quelques secondes).
3. Cliquez **Start** (panneau *Local web server*) et ouvrez l'URL locale ;
   partagez-la sur votre LAN. (Recliquez **Start** pour arrêter le serveur.)
4. Optionnel : renseignez **Web page title & description** (en haut à gauche) pour
   ajouter un titre à la page partagée et un aperçu enrichi quand le lien est collé
   dans Discord, etc.
5. Optionnel : cliquez **Publish** pour obtenir une URL publique
   `trycloudflare.com` pour le reste du lobby. Attendez la ligne de log
   *« link is now live »* avant de la partager ; utilisez le bouton de copie pour
   récupérer le lien, et recliquez **Publish** pour le mettre hors ligne.
6. Optionnel : collez une **Webhook URL** Discord (panneau Discord) pour poster dans
   votre salon en un clic — **Alert** (lien live public), **Standings** (image du
   tableau des points) ou **Rally** (image des résultats du rallye sélectionné).
   Voir [Publier sur Discord](#publier-sur-discord).
7. Optionnel (streaming) : serveur web lancé, utilisez le panneau *Overlays* —
   **Classification** et **Progress** ont chacun leurs réglages : afficher/masquer,
   always-on-top, lock click-through et opacité du fond. Pour OBS, cliquez **Copy OBS
   source URL** et ajoutez-la en Browser Source (voir [Capture avec OBS](#capture-avec-obs)).
   Pour poser directement par-dessus le jeu, affichez la fenêtre overlay et
   verrouillez-la en click-through.
8. Roulez. Les résultats apparaissent au fil des arrivées ; totaux et rangs se
   mettent à jour en direct. Groupez les spéciales en rallyes (*Stages — select to
   group*) et ouvrez l'onglet **Standings** de la page pour les résultats par rallye
   et les points de championnat. Utilisez **Reset session** pour vider le tableau
   entre deux événements (il conserve les nations, voitures décodées et le nom de la
   spéciale en cours pour qu'un redémarrage de la même spéciale ne reste pas sans nom).

## Capture avec OBS

Dans OBS, utilisez une **Browser Source**, pas une capture de fenêtre — elle rend la
page transparente directement. Une capture de fenêtre d'un overlay bureau ressort
**noire**, car le widget est composé par le GPU (WebView2) et la fenêtre elle-même est
transparente.

<img src="docs/overlay-classification.png" alt="Le widget overlay de classement" width="280">

<img src="docs/overlay-progress.png" alt="Le widget overlay de progression en direct" width="640">

<sub>Les deux widgets — classement et progression en direct — avec un fond à 90 %
d'opacité ; par-dessus le jeu, ils sont entièrement transparents. Données de
démonstration.</sub>

Dans le panneau *Overlays*, cliquez **Copy OBS source URL** pour le widget voulu, puis
dans OBS :

1. **Sources → + → Navigateur (Browser)**, collez l'URL. Ajoutez deux sources
   séparées pour les deux widgets : `…/?view=classification` et `…/?view=progress`.
2. Réglez la **Largeur** et la **Hauteur** de la source dans ses propriétés pour
   changer ce qui s'affiche — la page se re-rend à cette taille (ce n'est **pas**
   pareil que glisser le cadre dans la scène, qui ne fait qu'étirer le bitmap) :
   - **Classification** — augmentez la **Hauteur** pour révéler plus de lignes
   - **Progression** — augmentez la **Largeur** pour allonger la barre
3. Ensuite positionnez et zoomez dans la scène avec les poignées OBS classiques
   (Transformer).

Pas besoin de cliquer **Show** — les fenêtres overlay bureau sont indépendantes d'OBS ;
la Browser Source charge la page directement depuis le serveur. Seul le serveur web
doit tourner.

L'URL copiée embarque l'opacité du fond du widget (`?bg=`, depuis le panneau *Overlays*
— `bg=0` = totalement transparent). Les drapeaux se chargent depuis internet.

## Publier sur Discord

Le panneau Discord poste dans n'importe quel salon via un **webhook** — aucun bot à
installer, aucune application à autoriser, rien à faire tourner sur un serveur.

**Récupérer l'URL du webhook** (nécessite la permission *Gérer les webhooks* sur le
serveur) :

1. Dans Discord, ouvrez les paramètres du salon (⚙ à côté de son nom) →
   **Intégrations → Webhooks → Nouveau webhook** (accessible aussi via *Paramètres du
   serveur → Intégrations*).
2. Choisissez le salon cible, puis cliquez **Copier l'URL du webhook**.
3. Collez-la dans le panneau **Discord** de l'application. Elle est stockée chiffrée
   dans `ACRLiveTiming.config`, lisible uniquement par votre compte Windows — le
   bouton œil la révèle si besoin.

> ⚠️ **Traitez cette URL comme un secret.** Quiconque la possède peut poster
> n'importe quoi dans ce salon. En cas de fuite, supprimez le webhook dans Discord
> (ou *régénérez* son URL) — l'ancien lien meurt aussitôt.

**Personnalisez le messager.** Le nom et l'avatar affichés sur les messages sont ceux
du **webhook lui-même** — l'application ne les remplace jamais. Renommez-le (au nom
de votre communauté ou de votre championnat) et donnez-lui votre logo directement
dans les réglages du webhook côté Discord ; chaque message reste discrètement signé
*ACR Live Timing* dans l'en-tête de l'embed, avec un lien vers ce projet.

**Trois messages en un clic :**

| Bouton | Poste | Prérequis |
|---|---|---|
| **Alert** | le lien live public (titre + description de la page) avec un rappel 🔴 *Live now!* | un tunnel publié (**Publish**) |
| **Standings** | le tableau des points de championnat en image, daté | au moins un groupe rallye |
| **Rally** | les résultats des spéciales du rallye choisi en image, datés | le rallye sélectionné dans la liste |

L'envoi est manuel — rien n'est jamais posté sans un clic.

<img src="docs/discord-messages.png" alt="Messages Alert et Standings postés dans un salon Discord" width="480">

## Antivirus & SmartScreen

L'`.exe` publié n'est pas signé numériquement : au premier lancement, Windows peut
donc afficher l'un de deux avertissements. Les deux sont normaux pour un outil
open-source non signé — voici ce qu'ils signifient et quoi faire.

- **« Windows a protégé votre ordinateur » (SmartScreen).** Signifie seulement que
  l'éditeur est inconnu, pas qu'une menace a été trouvée. Cliquez **Informations
  complémentaires → Exécuter quand même**.
- **Détection antivirus / Defender.** L'application déclenche les détecteurs
  heuristiques car elle fait des choses que les malwares font aussi — elle ouvre un raw
  socket pour lire les paquets, tourne en élévation, et est livrée en exe monofichier
  auto-extractible. C'est un écouteur passif (voir **Vie privée & sécurité** plus
  haut) : elle n'envoie rien au jeu et ne modifie rien. Si Defender la met en
  quarantaine, c'est un faux positif — restaurez-la depuis la quarantaine, ou compilez
  l'exe vous-même depuis les sources (`publish.bat`).

**Vérifiez votre téléchargement.** Chaque release publie un checksum SHA-256.
Vérifiez que le fichier correspond avant de l'exécuter :

```
certutil -hashfile ACRLiveTiming.exe SHA256
```

Si vous préférez ne faire confiance à aucun binaire préconstruit, clonez le dépôt et
lancez `publish.bat` — l'exe obtenu est l'exe que vous exécutez.

## État & limites

| Élément | État |
|---|---|
| Trafic serveur→client | en clair (non chiffré), décodé |
| Temps splits + final par pilote | ✅ exact au ms vs écran en jeu, pénalités incluses |
| Nom de la spéciale | ✅ |
| Nationalité + voiture par pilote/spéciale | ✅ lus dès l'entrée au lobby depuis l'acteur participant du joueur (déterministe, aucun temps nécessaire) ; voiture conservée par spéciale et listée sans doublon sur la sélection ; liaison par le temps conservée en repli — validé sur captures d'écran |
| Liste complète des arrivants | ✅ scan multi-décalage de bits |
| Détection d'arrivée (masquer les temps intermédiaires) | ✅ événementielle via la phase de course répliquée (*Ended*), exacte à la ms, streamée en direct |
| Détection DNF | ✅ en direct sur la spéciale en cours (phase *Retire/Disqualify* de la voiture) ; le sort de chaque voiture est mémorisé à la clôture de la spéciale (abandon ou disparition en cours de run), déduction par les arrivées des autres gardée en repli ; le drapeau d'abandon des résultats répliqués est décodé aussi, donc même un abandon à zéro split est listé DNF |
| Progression de spéciale en direct | ✅ piste horizontale au-dessus du tableau, portée fixe derrière le leader par défaut (auto-ajustement leader↔dernier en option) ; marqueurs nommés dès le spawn (bloc d'identité PlayerState), repli premier split — peut être partielle pendant la première spéciale après accroche |
| Réglages web par spectateur | ✅ exclusion de spéciales, pénalité %, fenêtre de progression + portée fixe, masquer nations, Hide split times, filtre rallye, tri des colonnes, thème clair/sombre — recalculés côté client depuis les données brutes par spéciale ; propres à la session, reset vers la config hôte en un clic |
| Groupement en rallyes · onglet Standings · points | ✅ groupes définis par l'hôte, noms modifiables ; classement indépendant par rallye et points de championnat, partagés avec tous les spectateurs |
| Exports de résultats | ✅ boutons copie / CSV / PNG sur la page ; webhook Discord côté hôte (alerte live, tableau des points, résultats de rallye) |
| Phase lobby · heure de départ · prévision météo | ✅ décodés et affichés dans l'en-tête de la page |
| Positions / écarts live | 🟡 position live par voiture décodée, pas encore affichée en classement |

Développé contre **Assetto Corsa Rally 0.5.1**. Une mise à jour du jeu qui change le
format réseau — ou active un chiffrement — peut casser le décodage à tout moment.
L'outil lit **uniquement du clair** et ne tentera jamais de déchiffrer un flux
chiffré : si ACR chiffre son trafic, il le signale et le live timing s'arrête,
simplement.

## Structure du projet

```
src/
  Decode/    décodage protocole : parseur de réplication UE, FStrings, scanner de
             résultats, détecteur d'arrivée/progression streamé, liaison
             nation & voiture, météo
  Net/       sniffer raw socket, auto-détection serveur, enregistrement & replay pcap
  Model/     engine (machine à états) + matrice de session thread-safe
  Web/       serveur HTTP embarqué (/, /state) + interface single-page
  Tunnel/    lanceur cloudflared (version épinglée, checksum vérifié)
  Discord/   éditeur webhook (messages d'alerte + résultats)
  Updates/   vérification de release au démarrage (API GitHub)
  UI/        panneau de contrôle WPF + fenêtres overlay OBS transparentes + réglages
```

## Soutenir le projet

ACR Live Timing est gratuit et open source, développé sur mon temps personnel. Si
vous l'appréciez et voulez soutenir mon travail, vous pouvez apporter une
contribution — ça aide vraiment et ça fait toujours plaisir :

- ☕ [Ko-fi](https://ko-fi.com/florentdchamps)
- 💜 [GitHub Sponsors](https://github.com/sponsors/FlorentDChamps)

Une étoile sur le repo, un bug signalé ou simplement en parler autour de vous,
c'est déjà un vrai coup de pouce. 🙂

## Licence

Copyright (C) 2026 Florent DESCHAMPS.

Sous licence **GNU General Public License v3.0 ou ultérieure** (GPL-3.0-or-later) —
voir [LICENSE](LICENSE). Vous pouvez librement utiliser, étudier, partager et modifier
ce logiciel, mais toute version distribuée — forks modifiés inclus — doit rester open
source sous la même licence. Aucune redistribution fermée ou propriétaire n'est permise.
