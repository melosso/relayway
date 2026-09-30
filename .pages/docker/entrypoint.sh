#!/bin/bash

REPO_URL="https://github.com/melosso/relayway.git"
REPO_BRANCH="main"
SOURCE_FOLDER="/.pages/web"
DESTINATION_FOLDER="/usr/share/nginx/html"

echo "Cloning repository: $REPO_URL (Branch: $REPO_BRANCH)"
git clone -b "$REPO_BRANCH" "$REPO_URL" /tmp/repo

if [ -d "/tmp/repo$SOURCE_FOLDER" ]; then
    echo "Copying contents from $SOURCE_FOLDER to $DESTINATION_FOLDER"
    cp -R "/tmp/repo$SOURCE_FOLDER"/. "$DESTINATION_FOLDER"

    echo "Contents of $DESTINATION_FOLDER:"
    ls -la "$DESTINATION_FOLDER"
else
    echo "Error: Source folder $SOURCE_FOLDER not found in the repository"
    exit 1
fi

rm -rf /tmp/repo

nginx -g "daemon off;"
