#!/usr/bin/perl
# Reads TNC2 lines on stdin, prints FAP's decode of each as one JSON object per line.
use strict; use warnings;
use FindBin; use lib "$FindBin::Bin/perl-lib"; use lib $ENV{FAP_LIB} // '.';
use Ham::APRS::FAP qw(parseaprs);
use JSON::PP;
my $json = JSON::PP->new->canonical(1)->allow_blessed(1)->allow_nonref(1);
binmode STDIN; binmode STDOUT;
while (my $line = <STDIN>) {
    $line =~ s/\r?\n$//;
    my %h;
    parseaprs($line, \%h, 'accept_broken_mice' => 1);
    delete $h{origpacket};
    for my $k (keys %h) { $h{$k} = "$h{$k}" if ref($h{$k}) eq '' && defined $h{$k} && $h{$k} =~ /[^\x00-\x7f]/; }
    print $json->encode(\%h), "\n";
}
