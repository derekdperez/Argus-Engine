# Argus repo permission repair

Git does not store Linux file ownership. If an overlay ZIP is extracted with `sudo`,
files may become owned by `root`, which prevents the normal `ec2-user` account from
editing `.env`, `.gitignore`, or deployment scripts.

Run from the repo root:

```bash
bash deploy/fix-repo-permissions.sh
```

Or pass the repo path explicitly:

```bash
bash deploy/fix-repo-permissions.sh /home/ec2-user/argus-engine
```

The script:

- changes working-tree ownership back to the login user
- normalizes file and directory permissions
- restores executable bits for `.sh` deployment scripts
- protects local env files with `chmod 600`
- adds local env/log/artifact paths to `.gitignore`
- records executable bits for tracked shell scripts with `git update-index --chmod=+x`
