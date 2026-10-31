# Widget gratuit de consommation Claude Code et Codex

Widget Windows open source sous licence MIT : quotas Claude Code et ChatGPT Codex, détails des sessions observées, CPU, RAM, GPU et VRAM, réunis dans une carte de bureau personnalisable.

[Documentation complète](README.md) · [Guide pour les assistants IA](docs/AI-INSTALL.md) · [Confidentialité](PRIVACY.md)

## Installation

Prérequis : Windows 11, PowerShell 7.5+, Git et SDK .NET 10 pour Windows. Depuis PowerShell 7.5+ :

```powershell
git clone https://github.com/CustomDigitalServices-Kevin/claude-codex-quota-widget.git
cd claude-codex-quota-widget
pwsh -NoProfile -File ./install.ps1
pwsh -NoProfile -File ./configure-claude.ps1
```

La dernière commande affiche la configuration Claude proposée sans l’écrire. Après lecture, ajouter `-Apply` pour l’appliquer. Une barre d’état existante demande en plus `-ReplaceExisting`. Une sauvegarde précède le remplacement.

Chaque utilisateur se connecte lui-même à Claude Code et Codex. Ne copier aucun fichier d’authentification. Pour Codex, les journaux standards sont lus automatiquement ; le profil isolé facultatif pour les lectures directes est expliqué dans le guide IA.

## Ce qui est mesuré

- Quotas Claude fournis par la barre d’état, lorsque le compte et la session les exposent.
- Fenêtres de quotas Codex réellement disponibles.
- Sessions observées récemment : sujet, modèle, effort et agent lorsqu’ils sont connus.
- Processeur, mémoire vive, cartes graphiques et mémoire vidéo du PC.

La roue dentée règle les cartes, leur ordre et leur apparence. Le bouton + déplie les détails dans la carte. L’interface est en français.

Les compteurs matériels sont relevés environ toutes les quatre secondes. Les quotas dépendent des événements des fournisseurs et de leur fraîcheur. Le widget ne mesure pas toutes les conversations du site ChatGPT et ne garantit pas un nombre exact d’agents au travail.

Les données restent dans le profil Windows de l’utilisateur. Le dépôt ne contient aucun journal, compte, token ni historique personnel. Les exemples et tests sont fictifs.

## Désinstallation

Restaurer d’abord la configuration Claude avec `configure-claude.ps1 -Restore -BackupPath` et la sauvegarde imprimée lors de l’installation, puis lancer `pwsh -NoProfile -File ./uninstall.ps1`. Si aucun réglage n’existait avant installation, retirer seulement la propriété `statusLine` ajoutée, en conservant les autres réglages.

Projet communautaire indépendant, sans affiliation avec les fournisseurs cités.

