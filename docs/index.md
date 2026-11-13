---
_layout: landing
title: Aspirin
---

<div class="ap-home">

<section class="ap-hero ap-full">
<div class="ap-wrap">
<div>
<div class="ap-eyebrow"><b>Preview</b> For Aspire 13.6 on .NET 10. Not on NuGet yet.</div>
<h1>Windows service installs, <span>built from your AppHost</span></h1>
<p class="ap-lede">Aspirin turns the projects in an Aspire AppHost into an installation package for Windows servers
your pipeline can't reach. Build the package anywhere and hand it over. One script installs or updates the
services, and another rolls them back.</p>
<div class="ap-actions">
<a class="ap-btn ap-btn-primary" href="getting-started.md">Get started <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
<a class="ap-btn ap-btn-ghost" href="guides/installing.md">What the installer does</a>
<a class="ap-btn ap-btn-ghost" href="https://github.com/zacharydf7/aspirin"><i class="bi bi-github" aria-hidden="true"></i> GitHub</a>
</div>
</div>
<div class="ap-code">
<div class="ap-code-bar"><i></i><i></i><i></i><span>AppHost.cs</span></div>

[!code-csharp[](../samples/Sample.AppHost/AppHost.cs)]

</div>
</div>
</section>

<section class="ap-section">
<div class="ap-wrap">
<div class="ap-section-head">
<span class="ap-kicker">Why Aspirin</span>
<h2>The deployment scripts every team writes by hand, done once</h2>
<p>Publishing, packaging, installing and rolling back a Windows service usually lives in scripts copied from
project to project. Aspirin builds all of it from the AppHost you already have.</p>
</div>
<div class="ap-grid">
<a class="ap-card ap-card-featured" href="guides/installing.md">
<h3><i class="bi bi-box-seam" aria-hidden="true"></i>No access to the server needed</h3>
<p>The build produces a folder and nothing else. It never connects to the server, so it suits closed networks and
servers that only an operations team may touch. Whoever has access runs one script.</p>
</a>
<a class="ap-card" href="guides/settings.md">
<h3><i class="bi bi-sliders" aria-hidden="true"></i>Settings from your app model</h3>
<p>The environment Aspire gives a project locally becomes the service's settings on the server. Secrets stay out
of the package unless you choose to include them.</p>
</a>
<a class="ap-card" href="guides/installing.md#before-anything-changes">
<h3><i class="bi bi-shield-check" aria-hidden="true"></i>Checked before anything changes</h3>
<p>Before it touches the server, the installer verifies every file against its checksum and checks that every
setting has a value. <code>-WhatIf</code> lists the plan.</p>
</a>
<a class="ap-card" href="guides/installing.md#what-the-installer-does">
<h3><i class="bi bi-arrow-repeat" aria-hidden="true"></i>Safe to run twice</h3>
<p>The first run creates the service and later runs update it. Running the installer again never leaves a second
service behind.</p>
</a>
<a class="ap-card" href="guides/rolling-back.md">
<h3><i class="bi bi-arrow-counterclockwise" aria-hidden="true"></i>A rollback that knows what changed</h3>
<p>Every install backs up the files and the service's configuration first. Rolling back undoes the last install
and leaves alone any service that install never reached.</p>
</a>
<a class="ap-card" href="guides/folder-permissions.md">
<h3><i class="bi bi-lock" aria-hidden="true"></i>Folders locked down</h3>
<p>Folders the installer creates can only be changed by administrators, so an ordinary user can't swap a
service's files. It warns about existing folders that other accounts can change.</p>
</a>
<a class="ap-card" href="guides/pipelines.md">
<h3><i class="bi bi-terminal" aria-hidden="true"></i>No Docker, no Aspire CLI</h3>
<p>Building the package is one <code>dotnet run</code>. It works on Linux and Windows runners and needs nothing
but the .NET SDK.</p>
</a>
<a class="ap-card" href="reference/package-format.md">
<h3><i class="bi bi-file-earmark-code" aria-hidden="true"></i>Nothing hidden</h3>
<p>The package is a folder with a manifest, checksums and plain PowerShell scripts. An operator can read all of
it before running anything.</p>
</a>
</div>
</div>
</section>

<section class="ap-band ap-full">
<div class="ap-section ap-wrap">
<div class="ap-section-head">
<span class="ap-kicker">Documentation</span>
<h2>Find your way</h2>
<p>Start with the walkthrough, then use the guides for the details.</p>
</div>
<div class="ap-paths">
<div class="ap-path">
<h3><i class="bi bi-rocket-takeoff" aria-hidden="true"></i>Start here</h3>
<ul>
<li><a href="getting-started.md">Getting started</a></li>
<li><a href="reference/limitations.md">What isn't handled yet</a></li>
</ul>
</div>
<div class="ap-path">
<h3><i class="bi bi-boxes" aria-hidden="true"></i>Building packages</h3>
<ul>
<li><a href="guides/settings.md">Settings and secrets</a></li>
<li><a href="guides/pipelines.md">Building in a pipeline</a></li>
<li><a href="guides/several-services.md">Several services in one package</a></li>
</ul>
</div>
<div class="ap-path">
<h3><i class="bi bi-hdd-rack" aria-hidden="true"></i>On the server</h3>
<ul>
<li><a href="guides/installing.md">Installing a package</a></li>
<li><a href="guides/rolling-back.md">Rolling back</a></li>
<li><a href="guides/folder-permissions.md">Folder permissions</a></li>
</ul>
</div>
<div class="ap-path">
<h3><i class="bi bi-journal-code" aria-hidden="true"></i>Reference</h3>
<ul>
<li><a href="reference/package-format.md">The package format</a></li>
<li><a href="api/index.md">API reference</a></li>
</ul>
</div>
</div>
</div>
</section>

<section class="ap-cta ap-full">
<div class="ap-wrap">
<h2>From AppHost to a running service</h2>
<p>The walkthrough builds a package from a sample worker and installs it on your own machine.</p>
<div class="ap-actions">
<a class="ap-btn ap-btn-primary" href="getting-started.md">Get started <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
</div>
</div>
</section>

</div>
