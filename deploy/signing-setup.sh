#!/usr/bin/env bash
# Sets up code signing for deploy/package-installer.sh on a GitHub Actions runner, for the release's installer jobs and
# the signing dry run (docs/desktop.md#signing), so that both sign exactly the same way:
#
#   deploy/signing-setup.sh windows   # Azure Trusted Signing: writes the signing metadata, sets AZURE_SIGN_METADATA
#   deploy/signing-setup.sh macos     # a throw-away keychain with both Developer ID certificates and a notarytool
#                                     # profile; sets APPLE_SIGN_APP_IDENTITY, APPLE_SIGN_INSTALL_IDENTITY,
#                                     # APPLE_NOTARY_PROFILE and APPLE_KEYCHAIN
#
# The settings come from the environment, under the names the workflows give them:
#   windows: AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET (which vpk's DefaultAzureCredential reads itself),
#     SIGNING_ENDPOINT, SIGNING_ACCOUNT, SIGNING_PROFILE
#   macos: BUILD_CERTIFICATE_BASE64, INSTALLER_CERTIFICATE_BASE64, P12_PASSWORD, APPLE_ID, APPLE_PASSWORD, APPLE_TEAM,
#     APP_IDENTITY, INSTALL_IDENTITY
# Fails, naming the repository secrets and variables to set, when any is empty, and on macOS when the keychain does not
# hold the identities named. Writes the variables for package-installer.sh to $GITHUB_ENV. The macOS setup replaces the
# user's keychain search list, so it runs only on a CI runner (GITHUB_ACTIONS=true).
set -euo pipefail

platform="${1:-}"
case "$platform" in
  # Each pair is the environment variable and the repository secret or variable it comes from.
  windows)
    label=Windows
    settings="AZURE_TENANT_ID:AZURE_TENANT_ID AZURE_CLIENT_ID:AZURE_CLIENT_ID AZURE_CLIENT_SECRET:AZURE_CLIENT_SECRET
      SIGNING_ENDPOINT:AZURE_SIGNING_ENDPOINT SIGNING_ACCOUNT:AZURE_SIGNING_ACCOUNT SIGNING_PROFILE:AZURE_SIGNING_PROFILE"
    ;;
  macos)
    label=macOS
    settings="BUILD_CERTIFICATE_BASE64:APPLE_BUILD_CERTIFICATE_BASE64
      INSTALLER_CERTIFICATE_BASE64:APPLE_INSTALLER_CERTIFICATE_BASE64 P12_PASSWORD:APPLE_P12_PASSWORD
      APPLE_ID:APPLE_ID APPLE_PASSWORD:APPLE_APP_PASSWORD APPLE_TEAM:APPLE_TEAM_ID
      APP_IDENTITY:APPLE_SIGN_APP_IDENTITY INSTALL_IDENTITY:APPLE_SIGN_INSTALL_IDENTITY"
    ;;
  *)
    echo "Usage: $0 windows|macos" >&2
    exit 2
    ;;
esac

missing=()
for pair in $settings; do
  name="${pair%%:*}"
  if [ -z "${!name:-}" ]; then
    missing+=("${pair#*:}")
  fi
done
if [ ${#missing[@]} -gt 0 ]; then
  echo "::error::$label signing is not set up. Missing: ${missing[*]}. Set them as secrets and variables of the repository or the release environment (docs/desktop.md#signing)."
  exit 1
fi

temp="${RUNNER_TEMP:-$(mktemp -d)}"
env_file="${GITHUB_ENV:-/dev/stdout}"

case "$platform" in
  windows)
    printf '{"Endpoint":"%s","CodeSigningAccountName":"%s","CertificateProfileName":"%s"}' \
      "$SIGNING_ENDPOINT" "$SIGNING_ACCOUNT" "$SIGNING_PROFILE" > "$temp/metadata.json"
    echo "AZURE_SIGN_METADATA=$temp/metadata.json" >> "$env_file"
    ;;
  macos)
    if [ "${GITHUB_ACTIONS:-}" != true ]; then
      echo "deploy/signing-setup.sh macos replaces your keychain search list; it runs on CI runners only." >&2
      exit 2
    fi
    keychain="$temp/signing.keychain-db"
    password="$(uuidgen)"
    echo "$BUILD_CERTIFICATE_BASE64" | base64 --decode > "$temp/build.p12"
    echo "$INSTALLER_CERTIFICATE_BASE64" | base64 --decode > "$temp/installer.p12"
    security create-keychain -p "$password" "$keychain"
    security set-keychain-settings -lut 21600 "$keychain"
    security unlock-keychain -p "$password" "$keychain"
    security import "$temp/build.p12" -P "$P12_PASSWORD" -A -t cert -f pkcs12 -k "$keychain"
    security import "$temp/installer.p12" -P "$P12_PASSWORD" -A -t cert -f pkcs12 -k "$keychain"
    rm -f "$temp/build.p12" "$temp/installer.p12"
    # codesign and productsign may then use the keys without a prompt, which nobody on a runner could answer.
    security set-key-partition-list -S apple-tool:,apple: -s -k "$password" "$keychain" >/dev/null
    security list-keychain -d user -s "$keychain"
    # A wrong identity name would otherwise surface only deep inside vpk's signing. codesign takes part of the name
    # (without the team id, say), so part of it is enough here too.
    identities="$(security find-identity -v "$keychain")"
    for identity in "$APP_IDENTITY" "$INSTALL_IDENTITY"; do
      if ! grep -qF "$identity" <<<"$identities"; then
        echo "$identities"
        echo "::error::The keychain has no valid identity \"$identity\". Check APPLE_SIGN_APP_IDENTITY and APPLE_SIGN_INSTALL_IDENTITY against the certificates' names above (docs/desktop.md#signing)."
        exit 1
      fi
    done
    xcrun notarytool store-credentials --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM" --password "$APPLE_PASSWORD" \
      --keychain "$keychain" semantic-router-notary
    {
      echo "APPLE_SIGN_APP_IDENTITY=$APP_IDENTITY"
      echo "APPLE_SIGN_INSTALL_IDENTITY=$INSTALL_IDENTITY"
      echo "APPLE_NOTARY_PROFILE=semantic-router-notary"
      echo "APPLE_KEYCHAIN=$keychain"
    } >> "$env_file"
    ;;
esac
echo "$label signing is set up."
