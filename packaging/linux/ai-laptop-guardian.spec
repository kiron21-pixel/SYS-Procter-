# RPM for Fedora. Build after "dotnet publish ... -r linux-x64 --self-contained -o publish/":
#   rpmbuild -bb packaging/linux/ai-laptop-guardian.spec --define "_publishdir $PWD/publish" --define "_repo $PWD"
# NOT YET BUILT OR TESTED: see docs/PLATFORM_STATUS.md.
Name:           ai-laptop-guardian
Version:        0.2.1
Release:        1%{?dist}
Summary:        Security and maintenance status for your computer
License:        Proprietary
BuildArch:      x86_64
AutoReqProv:    no
Requires:       glibc, libgcc, libstdc++, zlib, libicu, fontconfig, libX11, libICE, libSM

%description
AI Laptop Guardian reports security status, performance and cleanup candidates.
It starts in Observe Only mode and changes nothing without explicit consent.

%install
mkdir -p %{buildroot}/opt/ai-laptop-guardian %{buildroot}%{_bindir} %{buildroot}%{_datadir}/applications %{buildroot}%{_datadir}/icons/hicolor/256x256/apps
cp -a %{_publishdir}/. %{buildroot}/opt/ai-laptop-guardian/
ln -s /opt/ai-laptop-guardian/ai-laptop-guardian %{buildroot}%{_bindir}/ai-laptop-guardian
install -m 644 %{_repo}/packaging/linux/ai-laptop-guardian.desktop %{buildroot}%{_datadir}/applications/
install -m 644 %{_repo}/assets/brand/logo-256.png %{buildroot}%{_datadir}/icons/hicolor/256x256/apps/ai-laptop-guardian.png

# No %%post/%%preun scriptlets: installing and removing run no code.
%files
/opt/ai-laptop-guardian
%{_bindir}/ai-laptop-guardian
%{_datadir}/applications/ai-laptop-guardian.desktop
%{_datadir}/icons/hicolor/256x256/apps/ai-laptop-guardian.png
