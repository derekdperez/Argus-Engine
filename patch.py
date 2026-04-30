import subprocess
import os
import sys

def run_local(command, description):
    print(f"--- {description} ---")
    try:
        # We use shell=True to support standard terminal commands
        subprocess.run(command, check=True, shell=True)
    except subprocess.CalledProcessError as e:
        print(f"❌ Error during: {description}")
        sys.exit(1)

def main():
    # 1. Sync with Remote
    # Ensure you aren't committing on top of an outdated branch
    run_local("git pull origin main", "Syncing local repo with origin")

    # 2. Clean Local Build Artifacts
    # Removes bin/obj folders in .NET and Go binaries to prevent 
    # stale files from being tracked or causing conflict.
    print("Cleaning local build artifacts...")
    run_local("find . -type d -name 'bin' -exec rm -rf {} +", "Removing .NET bin folders")
    run_local("find . -type d -name 'obj' -exec rm -rf {} +", "Removing .NET obj folders")
    
    # 3. Force Dependency Refresh
    # This ensures your .slnx or .sln and go.mod files are healthy
    # so the app server doesn't struggle with missing meta-data.
    run_local("dotnet restore", "Refreshing .NET dependencies")
    run_local("go mod tidy", "Cleaning up Go modules")

    # 4. Final Commit Preparation
    print("\nLocal repo is clean and dependencies are synced.")
    commit_msg = input("Enter commit message: ")
    
    run_local("git add .", "Staging changes")
    run_local(f'git commit -m "{commit_msg}"', "Committing changes")
    
    print("\nReady to push! Run 'git push' then pull/redeploy on the app server.")

if __name__ == "__main__":
    main()