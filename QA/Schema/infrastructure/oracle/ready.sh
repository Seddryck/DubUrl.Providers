#!/bin/bash
# The image invokes startup hooks after opening FREEPDB1 and resetting passwords.
# Native JSON and VECTOR need automatic segment space management.
set -e
sqlplus -L -s / as sysdba <<'SQL'
whenever sqlerror exit failure
alter session set container = FREEPDB1;

declare
    default_management varchar2(10);
    qa_tablespace_count number;
    datafile_path varchar2(513);
begin
    select t.segment_space_management into default_management
    from dba_tablespaces t join database_properties p on p.property_value = t.tablespace_name
    where p.property_name = 'DEFAULT_PERMANENT_TABLESPACE';
    if default_management <> 'AUTO' then
        select count(*) into qa_tablespace_count from dba_tablespaces where tablespace_name = 'DUBURL_QA_DATA';
        if qa_tablespace_count = 0 then
            select regexp_replace(file_name, '[^/]+$', 'duburl_qa_data01.dbf') into datafile_path
            from dba_data_files where tablespace_name = 'SYSTEM' and rownum = 1;
            execute immediate 'create tablespace DUBURL_QA_DATA datafile ' || dbms_assert.enquote_literal(datafile_path)
                || ' size 64M segment space management auto';
        end if;
        execute immediate 'alter database default tablespace DUBURL_QA_DATA';
    end if;
end;
/
exit
SQL
touch /tmp/duburl-qa-ready
