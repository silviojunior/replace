-- ANEXO

select * from anexo;

delete from anexo;

alter table anexo auto_increment = 1;

update anexo a
set a.filepath = null
where a.id in (select ams.anexo_id from anexo_migration_status ams)
;



-- MIGRATION_SETTINGS

select * from migration_settings ms;

update migration_settings 
set purge_files = false, 
limited_execution = true
where id = 1;



-- ANEXO_MIGRATION_STATUS

select * from anexo_migration_status order by anexo_id;

truncate table anexo_migration_status;

alter table anexo_migration_status auto_increment = 1;