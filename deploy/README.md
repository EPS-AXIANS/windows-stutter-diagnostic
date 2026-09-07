# Déploiement clé-en-main

Deux rôles : **toi** (tu prépares le pack une fois), **la personne distante** (elle clique).

---

## Toi — préparer le pack (une seule fois, sur Windows + .NET 8 SDK)

Prérequis : la solution doit **compiler**. Première passe à faire en suivant
[`../docs/INTEGRATION-NOTES.md`](../docs/INTEGRATION-NOTES.md) (rien n'a encore été compilé).

```powershell
# à la racine du dépôt
dotnet build StutterDiag.sln -c Release      # jusqu'à ce que ce soit vert
dotnet test  StutterDiag.sln -c Release

# fabrique le pack : artifacts\StutterDiag-Setup-<version>.zip
.\deploy\publish-release.ps1

# …et, en option, publie-le comme release GitHub (téléchargeable par lien) :
.\deploy\publish-release.ps1 -Release
```

Le pack `StutterDiag-Setup-<version>.zip` contient :

```
Lancer-StutterDiag.bat     ← elle double-clique là-dessus
StutterDiag.ps1            ← toute la logique (auto-élévation, menu FR)
LISEZ-MOI.txt              ← ses instructions, 3 étapes
app\                      ← build portable self-contained (Service + GUI + CLI,
                            AUCUN runtime .NET à installer sur sa machine)
```

Envoie-lui **ce seul fichier zip** (mail, WeTransfer, partage, lien de release…).

---

## Elle — installer (≈ 3 min, aucune compétence requise)

1. Clic droit sur le zip → **Extraire tout…**
2. Ouvrir le dossier → double-clic sur **`Lancer-StutterDiag.bat`**
3. Fenêtre de sécurité Windows → **Oui**
4. Dans le menu, taper **`1`** puis Entrée → *Installer et démarrer*
5. Message « Installation terminée » → c'est fait. L'ordi s'utilise normalement.

La surveillance tourne en service Windows : elle survit à la fermeture de
l'interface et aux redémarrages.

### Récupérer un rapport
Relancer `Lancer-StutterDiag.bat` → taper **`5`**. Le fichier
`StutterDiag-rapport-*.html` apparaît sur le Bureau ; elle te l'envoie.

### Autres touches du menu
`2` état · `3` (re)démarrer · `4` pause · `6` rouvrir l'interface · `7` désinstaller · `0` quitter

---

## Piloter à distance sans le menu

`StutterDiag.ps1` accepte aussi une action directe (utile si tu lui fais copier-coller
une ligne unique) :

```powershell
powershell -ExecutionPolicy Bypass -File StutterDiag.ps1 -Action install
powershell -ExecutionPolicy Bypass -File StutterDiag.ps1 -Action status
powershell -ExecutionPolicy Bypass -File StutterDiag.ps1 -Action report
powershell -ExecutionPolicy Bypass -File StutterDiag.ps1 -Action uninstall -RemoveData
```

Options : `-InstallDir <chemin>` (défaut `C:\StutterDiag`), `-PackageUrl <lien>`
ou `-PackageZip <chemin>` si l'app n'est pas dans le dossier `app\`.

---

## Notes

- **Admin :** demandé **une seule fois**, pour enregistrer le service. L'interface, elle,
  se lance sans élévation (le script la démarre via `explorer.exe`).
- **Sans admin du tout :** l'appli tourne quand même mais la session ETW noyau est
  désactivée (pas de latence DPC/ISR ni d'I/O disque par requête) ; l'interface le signale.
- **Données :** tout reste en local dans `C:\ProgramData\StutterDiag`. Aucune télémétrie.
- **Journal du script :** `C:\StutterDiag\deploy.log`.
- Repo privé : le téléchargement automatique depuis la release GitHub ne marche que si
  le repo est public **ou** si `gh` est installé et connecté sur sa machine. Le cas normal
  (dossier `app\` fourni dans le pack) ne touche pas au réseau.
