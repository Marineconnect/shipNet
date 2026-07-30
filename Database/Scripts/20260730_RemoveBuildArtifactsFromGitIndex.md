# Remove Build Artifacts From Git Index

Run these commands from the repository root if build outputs were committed by mistake.

```bash
git rm -r --cached --ignore-unmatch **/bin **/obj bin-build-* obj-build-* publish publish-* TestResults
git rm --cached --ignore-unmatch "*.dll" "*.pdb" "*.exe" "*.cache" "*.nupkg" "*.snupkg"
git add .gitignore
git status --short
```

These commands remove artifacts from Git tracking only. They do not delete production folders outside the repository.
