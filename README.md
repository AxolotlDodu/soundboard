# Soundboard

Soundboard de bureau pour Windows (C# / Avalonia), pensée pour être pilotée
à distance par un [macro pad](../macro-pad) personnalisé via une petite API
HTTP locale.

## Fonctionnalités

- Lecture de sons (`.mp3`, `.wav`, `.ogg`, `.flac`) avec un volume réglable
  indépendamment pour chaque son, modifiable même pendant la lecture
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
dans un fichier que n'importe quel client peut relire avant chaque appel.

Endpoints disponibles :

| Méthode | Route              | Description                                  |
|---------|---------------------|-----------------------------------------------|
| GET     | `/health`           | Ping simple pour vérifier que l'appli tourne  |
| GET     | `/sounds`           | Liste des sons configurés                     |
| GET     | `/devices`          | Liste des sorties audio disponibles           |
| POST    | `/play/{soundId}`   | Joue un son par son id stable                 |
| POST    | `/stop/{soundId}`   | Arrête toutes les instances d'un son          |
| POST    | `/stop-all`         | Arrête tous les sons en cours                 |

L'API n'est accessible que depuis la machine locale (pas d'exposition
réseau).

## Données & configuration

Stockées dans `%APPDATA%\Soundboard\` :

- `sounds.json` — liste des sons configurés (nom, chemin, volume)
- `port.txt` — port HTTP actif, relu par le macro pad à chaque appel

## Prérequis

- Windows 10/11 (x64)
- `bass.dll` (moteur audio [ManagedBass](https://github.com/ManagedBass/ManagedBass))
  — inclus dans les builds publiées / l'installeur ; pour un build manuel,
  télécharger la lib native sur [un4seen.com](https://www.un4seen.com/) et
  la placer à la racine du dossier du projet, à côté de `Soundboard.csproj`

## Build & lancement en développement

```bash
dotnet run
```