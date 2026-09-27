# Soundboard

Soundboard de bureau pour Windows (C# / Avalonia), pensée pour être pilotée
à distance par un [macro pad](https://github.com/AxolotlDodu/macro-pad) personnalisé via une petite API
HTTP locale.

## Fonctionnalités

- Lecture de sons (`.mp3`, `.wav`, `.ogg`, `.flac`) avec un volume réglable
  indépendamment pour chaque son, modifiable même pendant la lecture
- Découpage (trim) d'un son : fenêtre dédiée avec waveform, poignées de
  début/fin glissables, écoute de l'extrait, et choix d'écraser le son
  d'origine ou d'enregistrer l'extrait comme un nouveau son indépendant
- Deux sorties audio simultanées : une sortie **principale** et une sortie
  de **test**, activables séparément, pour comparer le rendu des volumes
  sur deux périphériques différents
- Détection de déconnexion du périphérique audio et reconnexion automatique
  sur le device par défaut du système
- Renommage des sons, sélection multiple (cases à cocher / tout
  sélectionner) et suppression
- Arrêt d'un son précis ou de tous les sons en cours (bouton ▶/⏹ par son,
  bouton global "Tout arrêter")
- Icône dans la zone de notification (tray) : clic gauche pour rouvrir la
  fenêtre, clic droit pour "Configuration" / "Quitter"
- Fermer la fenêtre ne quitte pas l'application : elle continue de tourner
  en arrière-plan (icône tray + API active) ; seul "Quitter" depuis le tray
  arrête vraiment le processus

## Intégration macro pad

Au démarrage, Soundboard lance un serveur HTTP local sur `127.0.0.1` (port
choisi automatiquement par l'OS pour éviter tout conflit) et écrit ce port
dans `%APPDATA%\Soundboard\port.txt`, que n'importe quel client relit avant
chaque appel.

Endpoints disponibles :

| Méthode | Route                          | Description                                          |
|---------|----------------------------------|-------------------------------------------------------|
| GET     | `/health`                       | Ping simple pour vérifier que l'appli tourne          |
| GET     | `/sounds`                       | Liste des sons configurés                             |
| GET     | `/devices`                      | Liste des sorties audio disponibles                   |
| POST    | `/play/{soundId}`               | Joue un son par son id stable                         |
| POST    | `/stop/{soundId}`               | Arrête toutes les instances d'un son                  |
| POST    | `/stop-all`                     | Arrête tous les sons en cours                         |
| GET     | `/volume`                       | Volume général actuel (0.0 à 1.0)                     |
| POST    | `/volume/{percent}`             | Fixe le volume général en absolu (0 à 100)            |
| POST    | `/volume/adjust/{deltaPercent}` | Ajuste le volume général d'un delta (ex. encodeur)    |

L'API n'est accessible que depuis la machine locale (pas d'exposition
réseau).

## Données & configuration

Stockées dans `%APPDATA%\Soundboard\` :

- `sounds.json` — liste des sons configurés (nom, chemin, volume, trim)
- `settings.json` — préférences (volume général, sorties audio sélectionnées)
- `port.txt` — port HTTP actif, relu par le macro pad à chaque appel
- `Sounds\` — copies des fichiers audio importés + extraits exportés (trim)

Rien de tout ça n'est effacé à la désinstallation : la configuration est
conservée si l'application est réinstallée par la suite.

## Prérequis

- Windows 10/11 (x64)
- `bass.dll` (moteur audio [ManagedBass](https://github.com/ManagedBass/ManagedBass))
  — inclus dans les builds publiées / l'installeur ; pour un build manuel,
  télécharger la lib native sur [un4seen.com](https://www.un4seen.com/) et
  la placer à la racine du dossier du projet, à côté de `Soundboard.csproj`

---

## Installation (utilisateur final)

1. Télécharger `Soundboard-Setup-<version>.exe` depuis la dernière release.
2. Lancer l'installeur (`PrivilegesRequired=lowest` : pas besoin des droits
   admin, installation par utilisateur).
3. Choisir éventuellement :
   - la création d'un raccourci sur le Bureau ;
   - le lancement automatique au démarrage de Windows.
4. Une fois installé, Soundboard se lance depuis le menu Démarrer ou le
   raccourci Bureau. L'icône apparaît dans la zone de notification.
5. Pour désinstaller : "Désinstaller Soundboard" dans le menu Démarrer ou
   via Windows (Applications installées). La configuration
   (`%APPDATA%\Soundboard`) est conservée.

Aucune donnée externe ni compte à configurer : tout fonctionne en local.

---

## Build & développement

Lancer directement depuis les sources :

```bash
dotnet run
```

### Générer l'installeur (release)

1. Publier l'application :

   ```bash
   dotnet publish -c Release -r win-x64 --self-contained
   ```

   Le résultat doit se retrouver dans le dossier attendu par
   `Soundboard-Setup.iss` (`MyPublishDir`, par défaut
   `..\bin\Release\net10.0\win-x64\publish`).

2. Compiler `Soundboard-Setup.iss` avec [Inno Setup](https://jrsoftware.org/isinfo.php)
   (IDE ou `iscc Soundboard-Setup.iss` en ligne de commande).
3. L'installeur est généré dans `installer/Output/`.

Avant de builder une nouvelle version :

- mettre à jour `MyAppVersion` dans `Soundboard-Setup.iss` ;
- ne jamais régénérer l'`AppId` (GUID) une fois publié, sous peine de casser
  la mise à jour en place pour les utilisateurs existants ;
- s'assurer que `bass.dll` est présent à la racine du projet avant le
  `dotnet publish` (non commité, voir Prérequis).