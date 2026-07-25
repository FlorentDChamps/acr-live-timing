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
  les doublons sont retirés.
- **Classement de session** — une colonne par run de spéciale, totaux et rangs.
  *Règle de seuil* configurable : un pilote absent d'une spéciale — ou plus lent que
  `meilleur temps × (1 + seuil %)` — est compté au temps seuil et signalé.
  Chaque spéciale peut être exclue des totaux d'une simple case à cocher. Une spéciale
  rejointe **en cours** n'est **pas** comptée — son départ a été manqué — mais son nom
  et les liaisons pilote/voiture sont quand même appris, pour que la spéciale suivante,
  capturée depuis le début, soit prête dès le premier split.
- **Affichage à l'arrivée** — le temps d'un pilote n'apparaît qu'une fois la ligne
  d'arrivée franchie, pour que les temps intermédiaires ne clignotent jamais ni ne
  « montent » au tableau. La détection est événementielle et exacte : le jeu
  réplique une phase de course par voiture (`RaceStateData.Phase`) ; le passage à
  *Ended* marque l'arrivée à l'instant précis, le chronomètre gelé correspondant au
  résultat affiché à la milliseconde. Streamée paquet par paquet : un replay révèle
  la même progression qu'en live, quelle que soit la vitesse de lecture. Une
  accroche de l'outil en cours de session ré-identifie le flux chrono à sa forme
  réseau — aucun redémarrage de spéciale nécessaire ; seules les captures réellement
  sans chrono (anciens enregistrements) se replient sur le gating par splits
  complets. Activable/désactivable dans le panneau Config — gating coupé, la page
  montre le dernier split cumulé de chaque pilote au fil de l'eau.
- **Affichage des DNF** — un pilote qui a posté des temps intermédiaires mais n'a
  jamais franchi la ligne est affiché **DNF** sur cette spéciale (compté au temps
  seuil, comme une absence). Sur la spéciale en cours, il apparaît dès que la phase
  de course de la voiture passe à *Retire/Disqualify* ; sur les spéciales passées,
  il est déduit une fois la spéciale close sans arrivée. Un pilote qui quitte avant
  les premiers splits reste affiché comme absent.
- **Progression de spéciale en direct** — une piste horizontale au-dessus du tableau
  montre la position live de chaque pilote sur la spéciale en cours, ajustée
  automatiquement entre le leader et la dernière voiture en course (portée max
  réglable dans le panneau Config). Chaque marqueur est nommé **dès le spawn, depuis
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
  l'hôte : exclure des spéciales du total, changer le seuil de pénalité %, régler la
  fenêtre de progression, masquer les nationalités, activer/couper le finish gating.
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
- **Interface bureau claire ou sombre** — l'application reprend le réglage clair/sombre
  de Windows au lancement, avec un interrupteur dans l'en-tête pour basculer à tout
  moment.
- **Enregistrement & replay** — enregistrement optionnel de la session dans un
  `.pcap` standard (lisible dans Wireshark), rejouable ensuite dans le même pipeline
  de décodage.
- **Mémorise votre configuration** — tout l'état de la fenêtre (options,
  titre/description de page, et pour chaque overlay : position, taille, always-on-top,
  lock et opacité) est sauvegardé dans un fichier `ACRLiveTiming.config` à côté de
  l'exe et restauré au prochain lancement.
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
  modèle de voiture. L'application n'affiche aucun identifiant de compte ni aucun
  champ de nom réel.
- **Qu'est-ce qui sort de votre machine ?** Rien, par défaut. Aucune télémétrie,
  aucun envoi. Seules connexions sortantes : (1) le téléchargement unique du binaire
  `cloudflared` épinglé et vérifié par checksum, depuis les releases GitHub
  officielles de Cloudflare — effectué automatiquement en arrière-plan au premier
  lancement, même si vous ne publiez jamais ; (2) le tunnel Cloudflare lui-même, *uniquement si vous
  cliquez Publish* — à partir de là, la page de classement est accessible à
  quiconque a le lien, jusqu'à fermeture de l'application. Si l'application meurt
  sans fermeture normale (crash, arrêt via le Gestionnaire des tâches), le process
  du tunnel peut lui survivre — vérifiez `cloudflared.exe` dans le Gestionnaire des
  tâches. (Les navigateurs qui consultent la page chargent aussi les drapeaux depuis
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
6. Optionnel (streaming) : serveur web lancé, utilisez le panneau *Overlays* —
   **Classification** et **Progress** ont chacun leurs réglages : afficher/masquer,
   always-on-top, lock click-through et opacité du fond. Pour OBS, cliquez **Copy OBS
   source URL** et ajoutez-la en Browser Source (voir [Capture avec OBS](#capture-avec-obs)).
   Pour poser directement par-dessus le jeu, affichez la fenêtre overlay et
   verrouillez-la en click-through.
7. Roulez. Les résultats apparaissent au fil des arrivées ; totaux et rangs se
   mettent à jour en direct. Utilisez **Reset session** pour vider le tableau entre
   deux événements (il conserve les nations, voitures décodées et le nom de la
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
| Détection DNF | 🟡 en direct sur la spéciale en cours (phase *Retire/Disqualify* de la voiture), déduit sur les spéciales closes des splits postés sans arrivée ; un abandon précoce (aucun split) reste affiché comme absent |
| Progression de spéciale en direct | ✅ piste horizontale auto-ajustée leader↔dernier au-dessus du tableau ; marqueurs nommés dès le spawn (bloc d'identité PlayerState), repli premier split — peut être partielle pendant la première spéciale après accroche |
| Réglages web par spectateur | ✅ exclusion de spéciales, pénalité %, fenêtre de progression, masquer nations, finish gating — recalculés côté client depuis les données brutes par spéciale ; propres à la session, reset vers la config hôte en un clic |
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
