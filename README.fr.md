# Widget gratuit de consommation Claude Code et Codex

Widget Windows open source sous licence MIT : quotas Claude Code et ChatGPT Codex, détails des sessions observées, CPU, RAM, GPU et VRAM, réunis dans une carte de bureau personnalisable.

[Documentation complète](README.md) · [Guide pour les assistants IA](docs/AI-INSTALL.md) · [Confidentialité](PRIVACY.md)

## Installation

Télécharger **QuotaWidget-Setup-1.0.2-win-x64.exe** dans la [dernière release GitHub](https://github.com/CustomDigitalServices-Kevin/claude-codex-quota-widget/releases/latest), puis suivre l’assistant. Le runtime .NET est inclus : aucune compilation ni installation du SDK .NET, de Git ou de PowerShell 7 n’est nécessaire. Version prévue pour Windows 11 x64, installation pour l’utilisateur courant.

L’assistant propose le démarrage à la connexion Windows. La connexion à Claude est facultative et décochée par défaut. Une barre d’état existante est conservée. L’intégration utilise Windows PowerShell 5.1, fourni avec Windows, uniquement pour lancer le producteur natif.

L’installateur communautaire est actuellement non signé. Windows peut afficher un avertissement de réputation ou d’éditeur. Des empreintes SHA256 sont fournies dans la release. Ne désactiver aucune protection Windows.

### Installation depuis les sources

Pour les développeurs : Windows 11, PowerShell 7.5+, Git et SDK .NET 10 pour Windows. Depuis PowerShell 7.5+ :

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

Avec l’installateur Windows, utiliser Paramètres > Applications > Applications installées > Quota Widget > Désinstaller. Les préférences, données locales et profils Codex sont conservés. L’intégration Claude est retirée uniquement si sa commande appartient encore à cette installation. Les autres réglages et modifications ultérieures sont préservés.

Avec l’ancien installateur depuis les sources, restaurer d’abord Claude avec `configure-claude.ps1 -Restore -BackupPath` et la sauvegarde imprimée, puis lancer `pwsh -NoProfile -File ./uninstall.ps1`.

Projet communautaire indépendant, sans affiliation avec les fournisseurs cités.

