/* Грубая очистка таблицы
SET FOREIGN_KEY_CHECKS = 0;

TRUNCATE TABLE `virt_assist`.`in_progress`;
TRUNCATE TABLE `virt_assist`.`workers_exp`;
TRUNCATE TABLE `virt_assist`.`people`;
TRUNCATE TABLE `virt_assist`.`sub_tasks`;
TRUNCATE TABLE `virt_assist`.`projects`;
TRUNCATE TABLE `virt_assist`.`proffesions`;
TRUNCATE TABLE `virt_assist`.`roles`;

SET FOREIGN_KEY_CHECKS = 1;
*/




INSERT INTO `virt_assist`.`roles` (`name`, `desc`) VALUES
('Сотрудник', 'Эта роль позваляет смотреть и редактировать некоторую информацию о себе, а также смотреть информацию о проектах.'),
('Руководитель', 'Эта роль расширяет возможности роли "сотрудник", добавляя возможность назначать сотрудника на проект и смотреть всю информацию, которая хранится в базе данны.');

#select *  from `virt_assist`.`roles`;

INSERT INTO `virt_assist`.`proffesions` (`name`, `desc`) VALUES
('Frontend Developer', 'Разработчик пользовательского интерфейса.'),
('Backend Developer', 'Разработчик серверной логики.'),
('QA Engineer', 'Специалист по качеству и тестированию.'),
('Project Manager', 'Управляет проектами и командами.');

#select *  from `virt_assist`.`proffesions`;


INSERT INTO `virt_assist`.`projects` (`name`, `desc`) VALUES
('Проект A', 'Первый проект, направленный на создание веб-приложения.'),
('Проект B', 'Новый продукт для упрощения бизнес-процессов.'),
('Проект C', 'Разработка мобильного приложения для клиентов.');

#select *  from `virt_assist`.`projects`;

INSERT INTO `virt_assist`.`sub_tasks` (`project_id`, `desc`) VALUES
(1, 'Создание макета интерфейса.'),
(1, 'Разработка функционала.'),
(2, 'Анализ требований.'),
(3, 'Тестирование приложения.');

#select *  from `virt_assist`.`sub_tasks`;


INSERT INTO `virt_assist`.`people` (`fio`, `date_b`, `exp_years`, `email`, `desc`, `tg`, `r_id`) VALUES
('Иван Иванов', '1990-01-15', 5, 'ivan@company.com', 'Имеет высокий уровень профессионализма и ответственность. Отличные навыки работы в команде и быстрой разработки.', 'ivan_tg', 1),
('Петр Петров', '1985-02-20', 10, 'petr@company.com', 'Имеет отличные управленческие навыки, способен эффективно организовывать работу команды. Уверенный в принятии решений.', 'petr_tg', 2),
('Светлана Сидорова', '1992-03-25', 3, 'sveta@company.com', 'Тщательный и аккуратный тестировщик. Обладает хорошими аналитическими способностями и вниманием к деталям.', 'sveta_tg', 1),
('Анна Анистратова', '1988-06-30', 7, 'anna@company.com', 'Обладает отличными аналитическими и коммуникативными навыками. Умеет находить общий язык с различными группами.', 'anna_tg', 1);


#select *  from `virt_assist`.`people`;


INSERT INTO `virt_assist`.`workers_exp` (`peple_id`, `prof_id`, `date_start`, `date_end`) VALUES
(1, 1, '2020-01-01', '2023-01-01'),  -- Иван Иванов стал Frontend Developer
(1, 2, '2023-01-01', NULL),  -- Иван Иванов перешел на Backend Developer
(2, 2, '2015-05-01', '2021-03-01'),  -- Петр Петров стал Backend Developer
(2, 4, '2021-03-01', NULL),  -- Петр Петров стал Project Manager
(3, 3, '2021-06-01', NULL),  -- Светлана Сидорова стала QA Engineer
(4, 4, '2019-09-01', NULL);  -- Анна Анистратова стала Project Manager


#select *  from `virt_assist`.`workers_exp`;


INSERT INTO `virt_assist`.`in_progress` (`st_ip`, `peple_id`, `status`) VALUES
(1, 1, 'В процессе'),  -- Иван Иванов работает над задачей по созданию макета интерфейса.
(2, 1, 'Завершена'),   -- Иван Иванов завершил разработку функционала.
(3, 2, 'В процессе'),  -- Петр Петров анализирует требования.
(4, 3, 'В процессе');  -- Светлана Сидорова тестирует приложение.



#select *  from `virt_assist`.`in_progress`;
#//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
# Получение информации о подзадачах и соответствующих проектах
select 
    st.st_ip, 
    st.desc as sub_task_desc, 
    p.name as project_name,
    p.desc as project_desc
from 
    `virt_assist`.`sub_tasks` st
inner join 
    `virt_assist`.`projects` p on st.project_id = p.project_id;


# Получение информации о работниках, их ролях и профессиях
select 
    pe.fio, 
    r.name as role_name, 
    p.name as profession_name,
    we.date_start,
    we.date_end
from 
    `virt_assist`.`people` pe
join 
    `virt_assist`.`roles` r on pe.r_id = r.r_id
join 
    `virt_assist`.`workers_exp` we on pe.peple_id = we.peple_id
join 
    `virt_assist`.`proffesions` p on we.prof_id = p.prof_id;

# Получение подзадач с их статусами и информация о работниках

select 
    st.st_ip, 
    st.desc as sub_task_desc, 
    ip.status, 
    pe.fio,
    pr.name as project
from 
    `virt_assist`.`in_progress` ip
join 
    `virt_assist`.`sub_tasks` st on ip.st_ip = st.st_ip
join 
    `virt_assist`.`people` pe on ip.peple_id = pe.peple_id
join 
    `virt_assist`.`projects` pr on st.project_id = pr.project_id;

# Cчитает количество сотрудников назначенных на проект

select 
    p.project_id, 
    p.name as project_name, 
    COUNT(ip.peple_id) as emp_count
from 
    `virt_assist`.`projects` p
left join 
    `virt_assist`.`in_progress` ip on p.project_id = ip.st_ip
group by 
    p.project_id, p.name;
    
# Cчитает количество сотрудников назначенных на подзадачи

select 
    st.project_id, 
    st.st_ip, 
    st.desc as sub_task_desc, 
    COUNT(ip.peple_id) as emp_count
from 
    `virt_assist`.`sub_tasks` st
left join 
    `virt_assist`.`in_progress` ip on st.st_ip = ip.st_ip
group by 
    st.project_id, st.st_ip, st.desc;

