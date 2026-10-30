# Third-party notices

`rcpFlatten` lie et/ou utilise plusieurs composants tiers. Le code source de ce
dépôt (dossiers `RcpFlattenCli/` et `RcpFlattenPlugin/`) est distribué sous
licence MIT (voir `LICENSE`), mais les composants tiers ci-dessous restent
soumis à leur propre licence.

## Autodesk ReCap SDK

- **Composant** : Autodesk ReCap SDK (en-têtes sous `Include/` et bibliothèques
  d'import sous `Lib/`, ainsi que les DLL et modules nécessaires à l'exécution).
- **Licence** : propriétaire Autodesk, soumise au contrat ReCap SDK / EULA
  Autodesk Developer Network.
- **Conséquences pratiques** :
  - Aucune partie du ReCap SDK n'est redistribuée dans ce dépôt.
  - L'utilisateur final doit avoir accepté l'EULA Autodesk pour utiliser le
    SDK et les DLL associées.
  - Les binaires publiés via GitHub Releases ne contiennent **pas** les DLL
    Autodesk ; voir le `README.md` pour la marche à suivre.
- Référence : https://aps.autodesk.com/

## Autodesk AutoCAD

- **Composant** : bibliothèques .NET `accoremgd.dll` et `acdbmgd.dll`,
  référencées à la compilation uniquement.
- **Licence** : propriétaire Autodesk, soumise au contrat de licence AutoCAD.
- **Conséquences pratiques** : ces bibliothèques ne sont pas redistribuées ; le
  plug-in s'exécute dans une installation d'AutoCAD 2027 sous licence.

## Microsoft Visual C++ Runtime

- **Composant** : `vcruntime140.dll`, `msvcp140.dll`, etc.
- **Licence** : licence Microsoft Visual Studio. La redistribution est
  autorisée via le « Microsoft Visual C++ Redistributable » publié par
  Microsoft, ou en demandant à l'utilisateur final de l'installer lui-même.
- Référence : https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files

---

Pour toute question sur le respect de ces licences, ouvrez une *issue* sur
le dépôt GitHub.
